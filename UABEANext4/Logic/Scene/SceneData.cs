using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using UABEANext4.AssetWorkspace;
using UABEANext4.Logic.Mesh;

namespace UABEANext4.Logic.Scene;

/// <summary>
/// Loads and manages scene data from Unity asset files.
/// </summary>
public class SceneData
{
    public List<SceneObject> RootObjects { get; } = new();
    public List<SceneObject> AllObjects { get; } = new();

    private readonly Workspace _workspace;
    private readonly Dictionary<long, SceneObject> _pathIdToObject = new();
    private readonly Action<string>? _logAction;

    public SceneData(Workspace workspace, Action<string>? logAction = null)
    {
        _workspace = workspace;
        _logAction = logAction;
    }

    private void Log(string message)
    {
        _logAction?.Invoke(message);
    }

    public void LoadFromFile(AssetsFileInstance fileInst)
    {
        if (fileInst == null)
        {
            Log("LoadFromFile: fileInst is null");
            throw new ArgumentNullException(nameof(fileInst), "File instance cannot be null");
        }

        if (fileInst.file == null)
        {
            Log("LoadFromFile: fileInst.file is null");
            throw new ArgumentException("File instance has null file", nameof(fileInst));
        }

        RootObjects.Clear();
        AllObjects.Clear();
        _pathIdToObject.Clear();

        Log($"Starting LoadFromFile for: {fileInst.name}");

        // First pass: Create all scene objects with transforms
        var transformInfos = fileInst.file.GetAssetsOfType(AssetClassID.Transform)
            .Concat(fileInst.file.GetAssetsOfType(AssetClassID.RectTransform))
            .ToList();

        Log($"Found {transformInfos.Count} transforms");

        var goInfos = fileInst.file.GetAssetsOfType(AssetClassID.GameObject).ToList();
        Log($"Found {goInfos.Count} GameObjects");

        var goPathIdToInfo = goInfos.ToDictionary(g => g.PathId);

        // Map transform PathId to its parent PathId
        var tfmParentMap = new Dictionary<long, long>();
        var tfmToGoMap = new Dictionary<long, long>();
        var tfmChildrenMap = new Dictionary<long, List<long>>();

        foreach (var tfmInfo in transformInfos)
        {
            var tfmBf = _workspace.GetBaseField(fileInst, tfmInfo.PathId);
            if (tfmBf == null) continue;

            var parentPathId = tfmBf["m_Father"]["m_PathID"].AsLong;
            tfmParentMap[tfmInfo.PathId] = parentPathId;

            var goPtr = tfmBf["m_GameObject"];
            var goPathId = goPtr["m_PathID"].AsLong;
            tfmToGoMap[tfmInfo.PathId] = goPathId;

            // Read transform data
            var localPos = ReadVector3(tfmBf["m_LocalPosition"]);
            var localRot = ReadQuaternion(tfmBf["m_LocalRotation"]);
            var localScale = ReadVector3(tfmBf["m_LocalScale"]);

            string goName = "[Unknown]";
            AssetInst? goAsset = null;
            if (goPathIdToInfo.TryGetValue(goPathId, out var goInfo))
            {
                goAsset = _workspace.GetAssetInst(fileInst, 0, goInfo.PathId);
                var goBf = _workspace.GetBaseField(fileInst, goInfo.PathId);
                if (goBf != null)
                {
                    goName = goBf["m_Name"].AsString;
                }
            }

            var sceneObj = new SceneObject
            {
                Name = goName,
                PathId = tfmInfo.PathId,
                GameObjectAsset = goAsset,
                LocalPosition = localPos,
                LocalRotation = localRot,
                LocalScale = localScale
            };

            _pathIdToObject[tfmInfo.PathId] = sceneObj;
            AllObjects.Add(sceneObj);

            // Track children
            var childrenArr = tfmBf["m_Children.Array"];
            var childIds = new List<long>();
            foreach (var child in childrenArr)
            {
                childIds.Add(child["m_PathID"].AsLong);
            }
            tfmChildrenMap[tfmInfo.PathId] = childIds;
        }

        // Second pass: Build hierarchy
        Log("Building hierarchy...");
        foreach (var kvp in _pathIdToObject)
        {
            var tfmPathId = kvp.Key;
            var sceneObj = kvp.Value;

            if (tfmParentMap.TryGetValue(tfmPathId, out var parentPathId) && parentPathId != 0)
            {
                if (_pathIdToObject.TryGetValue(parentPathId, out var parentObj))
                {
                    sceneObj.Parent = parentObj;
                    parentObj.Children.Add(sceneObj);
                }
            }
            else
            {
                RootObjects.Add(sceneObj);
            }
        }

        Log($"Built hierarchy: {AllObjects.Count} objects, {RootObjects.Count} root objects");

        // Third pass: Load meshes and materials for objects with MeshFilter/MeshCollider/SkinnedMeshRenderer
        Log("Loading meshes and materials...");
        var meshFilterInfos = fileInst.file.GetAssetsOfType(AssetClassID.MeshFilter).ToList();
        var meshRendererInfos = fileInst.file.GetAssetsOfType(AssetClassID.MeshRenderer).ToList();
        var meshColliderInfos = fileInst.file.GetAssetsOfType(AssetClassID.MeshCollider).ToList();
        var skinnedMeshRendererInfos = fileInst.file.GetAssetsOfType(AssetClassID.SkinnedMeshRenderer).ToList();

        Log($"Found {meshFilterInfos.Count} MeshFilters, {meshRendererInfos.Count} MeshRenderers, {meshColliderInfos.Count} MeshColliders, {skinnedMeshRendererInfos.Count} SkinnedMeshRenderers");

        // Map GameObject PathId to MeshFilter/MeshRenderer/MeshCollider/SkinnedMeshRenderer
        var goToMeshFilter = new Dictionary<long, AssetFileInfo>();
        var goToMeshRenderer = new Dictionary<long, AssetFileInfo>();
        var goToMeshCollider = new Dictionary<long, AssetFileInfo>();
        var goToSkinnedMeshRenderer = new Dictionary<long, AssetFileInfo>();

        // Track which GameObjects have batched/combined meshes (so we skip their MeshFilter)
        var batchedGameObjects = new HashSet<long>();

        foreach (var mfInfo in meshFilterInfos)
        {
            var mfBf = _workspace.GetBaseField(fileInst, mfInfo.PathId);
            if (mfBf == null) continue;

            var goPathId = mfBf["m_GameObject"]["m_PathID"].AsLong;
            goToMeshFilter[goPathId] = mfInfo;
        }

        foreach (var mrInfo in meshRendererInfos)
        {
            var mrBf = _workspace.GetBaseField(fileInst, mrInfo.PathId);
            if (mrBf == null) continue;

            var goPathId = mrBf["m_GameObject"]["m_PathID"].AsLong;
            goToMeshRenderer[goPathId] = mrInfo;

            // Check if this object uses a batched/combined mesh
            var staticBatchInfo = mrBf["m_StaticBatchInfo"];
            if (staticBatchInfo != null && !staticBatchInfo.IsDummy)
            {
                var subMeshCount = staticBatchInfo["subMeshCount"].AsInt;
                if (subMeshCount > 0)
                {
                    // This object's MeshFilter points to a combined mesh - mark it as batched
                    batchedGameObjects.Add(goPathId);
                }
            }
        }

        foreach (var mcInfo in meshColliderInfos)
        {
            var mcBf = _workspace.GetBaseField(fileInst, mcInfo.PathId);
            if (mcBf == null) continue;

            var goPathId = mcBf["m_GameObject"]["m_PathID"].AsLong;
            goToMeshCollider[goPathId] = mcInfo;
        }

        foreach (var smrInfo in skinnedMeshRendererInfos)
        {
            var smrBf = _workspace.GetBaseField(fileInst, smrInfo.PathId);
            if (smrBf == null) continue;

            var goPathId = smrBf["m_GameObject"]["m_PathID"].AsLong;
            goToSkinnedMeshRenderer[goPathId] = smrInfo;
        }

        // Load mesh data for each scene object
        // Priority order:
        // 1. SkinnedMeshRenderer mesh (for animated/skinned objects)
        // 2. MeshCollider mesh (if exists) - most accurate representation for batched objects
        // 3. MeshFilter mesh (only for non-batched objects) - original mesh
        // Note: Batched objects without MeshCollider will not show a mesh (combined meshes look fragmented)
        int meshesLoaded = 0;
        int meshLoadErrors = 0;
        int colliderMeshesLoaded = 0;
        int skinnedMeshesLoaded = 0;
        foreach (var sceneObj in AllObjects)
        {
            var goPathId = tfmToGoMap.GetValueOrDefault(sceneObj.PathId, 0);
            if (goPathId == 0) continue;

            bool meshLoaded = false;

            // First priority: Try SkinnedMeshRenderer mesh (for animated objects)
            if (goToSkinnedMeshRenderer.TryGetValue(goPathId, out var smrInfo))
            {
                var smrBf = _workspace.GetBaseField(fileInst, smrInfo.PathId);
                if (smrBf != null)
                {
                    var meshPtr = smrBf["m_Mesh"];
                    if (meshPtr != null && !meshPtr.IsDummy)
                    {
                        var meshPathId = meshPtr["m_PathID"].AsLong;
                        var meshFileId = meshPtr["m_FileID"].AsInt;

                        if (meshPathId != 0)
                        {
                            try
                            {
                                var meshBf = _workspace.GetBaseField(fileInst, meshFileId, meshPathId);
                                if (meshBf != null)
                                {
                                    var version = fileInst.file.Metadata.UnityVersion;
                                    sceneObj.Mesh = new MeshObj(fileInst, meshBf, new UnityVersion(version));

                                    if (sceneObj.Mesh?.UVs != null && sceneObj.Mesh.UVs.Length > 0 && sceneObj.Mesh.UVs[0] != null)
                                    {
                                        sceneObj.UVs = sceneObj.Mesh.UVs[0];
                                    }
                                    // Load UV1 for lightmaps
                                    if (sceneObj.Mesh?.UVs != null && sceneObj.Mesh.UVs.Length > 1 && sceneObj.Mesh.UVs[1] != null)
                                    {
                                        sceneObj.LightmapUVs = sceneObj.Mesh.UVs[1];
                                    }

                                    // Mark as skinned mesh
                                    sceneObj.IsSkinnedMesh = true;

                                    // Get root bone for proper positioning
                                    var rootBonePtr = smrBf["m_RootBone"];
                                    if (rootBonePtr != null && !rootBonePtr.IsDummy)
                                    {
                                        var rootBonePathId = rootBonePtr["m_PathID"].AsLong;
                                        if (rootBonePathId != 0 && _pathIdToObject.TryGetValue(rootBonePathId, out var rootBoneObj))
                                        {
                                            sceneObj.RootBone = rootBoneObj;
                                            Log($"SkinnedMesh '{sceneObj.Name}' uses root bone '{rootBoneObj.Name}'");
                                        }
                                    }

                                    meshLoaded = true;
                                    skinnedMeshesLoaded++;
                                    meshesLoaded++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"SkinnedMeshRenderer mesh load failed for '{sceneObj.Name}': {ex.Message}");
                                meshLoadErrors++;
                            }
                        }
                    }

                    // Load texture from SkinnedMeshRenderer materials
                    if (meshLoaded)
                    {
                        var materialsArr = smrBf["m_Materials.Array"];
                        if (materialsArr != null && !materialsArr.IsDummy && materialsArr.Children.Count > 0)
                        {
                            var matPtr = materialsArr[0];
                            var matPathId = matPtr["m_PathID"].AsLong;
                            var matFileId = matPtr["m_FileID"].AsInt;

                            if (matPathId != 0)
                            {
                                try
                                {
                                    LoadTextureFromMaterial(fileInst, matFileId, matPathId, sceneObj);
                                }
                                catch (Exception ex)
                                {
                                    Log($"Texture load failed for skinned mesh '{sceneObj.Name}': {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }

            // Second priority: Try MeshCollider mesh (best representation for static batched objects)
            if (!meshLoaded && goToMeshCollider.TryGetValue(goPathId, out var mcInfo))
            {
                var mcBf = _workspace.GetBaseField(fileInst, mcInfo.PathId);
                if (mcBf != null)
                {
                    var meshPtr = mcBf["m_Mesh"];
                    if (meshPtr != null && !meshPtr.IsDummy)
                    {
                        var meshPathId = meshPtr["m_PathID"].AsLong;
                        var meshFileId = meshPtr["m_FileID"].AsInt;

                        if (meshPathId != 0)
                        {
                            try
                            {
                                var meshBf = _workspace.GetBaseField(fileInst, meshFileId, meshPathId);
                                if (meshBf != null)
                                {
                                    var version = fileInst.file.Metadata.UnityVersion;
                                    sceneObj.Mesh = new MeshObj(fileInst, meshBf, new UnityVersion(version));

                                    if (sceneObj.Mesh?.UVs != null && sceneObj.Mesh.UVs.Length > 0 && sceneObj.Mesh.UVs[0] != null)
                                    {
                                        sceneObj.UVs = sceneObj.Mesh.UVs[0];
                                    }
                                    // Load UV1 for lightmaps
                                    if (sceneObj.Mesh?.UVs != null && sceneObj.Mesh.UVs.Length > 1 && sceneObj.Mesh.UVs[1] != null)
                                    {
                                        sceneObj.LightmapUVs = sceneObj.Mesh.UVs[1];
                                    }
                                    meshLoaded = true;
                                    colliderMeshesLoaded++;
                                    meshesLoaded++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"MeshCollider mesh load failed for '{sceneObj.Name}': {ex.Message}");
                                meshLoadErrors++;
                            }
                        }
                    }
                }
            }

            // Second priority: Try MeshFilter mesh (if not batched or no collider mesh)
            if (!meshLoaded && goToMeshFilter.TryGetValue(goPathId, out var mfInfo))
            {
                // Check if this is a batched object - if so, skip MeshFilter (it points to combined mesh)
                var isBatched = batchedGameObjects.Contains(goPathId);

                if (!isBatched)
                {
                    var mfBf = _workspace.GetBaseField(fileInst, mfInfo.PathId);
                    if (mfBf != null)
                    {
                        var meshPtr = mfBf["m_Mesh"];
                        if (meshPtr != null && !meshPtr.IsDummy)
                        {
                            var meshPathId = meshPtr["m_PathID"].AsLong;
                            var meshFileId = meshPtr["m_FileID"].AsInt;

                            if (meshPathId != 0)
                            {
                                try
                                {
                                    var meshBf = _workspace.GetBaseField(fileInst, meshFileId, meshPathId);
                                    if (meshBf != null)
                                    {
                                        var version = fileInst.file.Metadata.UnityVersion;
                                        sceneObj.Mesh = new MeshObj(fileInst, meshBf, new UnityVersion(version));

                                        if (sceneObj.Mesh?.UVs != null && sceneObj.Mesh.UVs.Length > 0 && sceneObj.Mesh.UVs[0] != null)
                                        {
                                            sceneObj.UVs = sceneObj.Mesh.UVs[0];
                                        }
                                        // Load UV1 for lightmaps
                                        if (sceneObj.Mesh?.UVs != null && sceneObj.Mesh.UVs.Length > 1 && sceneObj.Mesh.UVs[1] != null)
                                        {
                                            sceneObj.LightmapUVs = sceneObj.Mesh.UVs[1];
                                        }
                                        meshLoaded = true;
                                        meshesLoaded++;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"MeshFilter mesh load failed for '{sceneObj.Name}': {ex.Message}");
                                    meshLoadErrors++;
                                }
                            }
                        }
                    }
                }
            }

            // Load texture from material
            if (goToMeshRenderer.TryGetValue(goPathId, out var mrInfo))
            {
                var mrBf = _workspace.GetBaseField(fileInst, mrInfo.PathId);
                if (mrBf == null) continue;

                var materialsArr = mrBf["m_Materials.Array"];
                if (materialsArr != null && !materialsArr.IsDummy && materialsArr.Children.Count > 0)
                {
                    var matPtr = materialsArr[0];
                    var matPathId = matPtr["m_PathID"].AsLong;
                    var matFileId = matPtr["m_FileID"].AsInt;

                    if (matPathId != 0)
                    {
                        try
                        {
                            LoadTextureFromMaterial(fileInst, matFileId, matPathId, sceneObj);
                        }
                        catch (Exception ex)
                        {
                            Log($"Texture load failed for '{sceneObj.Name}': {ex.Message}");
                            // Texture loading failed, skip
                        }
                    }
                }
            }
        }

        Log($"Loaded {meshesLoaded} meshes ({skinnedMeshesLoaded} skinned, {colliderMeshesLoaded} from MeshColliders, {meshLoadErrors} errors)");

        // Compute world matrices and bounds
        Log("Computing world matrices and bounds...");
        foreach (var root in RootObjects)
        {
            root.ComputeWorldMatrix();
        }

        // Second pass: Update skinned mesh positions using root bone world matrices
        // This ensures the root bone's world matrix is available before we use it
        foreach (var obj in AllObjects)
        {
            if (obj.IsSkinnedMesh && obj.RootBone != null)
            {
                obj.WorldMatrix = obj.RootBone.WorldMatrix;
            }
        }

        foreach (var obj in AllObjects)
        {
            obj.ComputeBounds();
        }

        Log($"LoadFromFile completed: {AllObjects.Count} objects total");
    }

    private void LoadTextureFromMaterial(AssetsFileInstance fileInst, int matFileId, long matPathId, SceneObject sceneObj)
    {
        var matBf = _workspace.GetBaseField(fileInst, matFileId, matPathId);
        if (matBf == null) return;

        // Try to find _MainTex in the saved properties
        var savedProps = matBf["m_SavedProperties"];
        if (savedProps == null || savedProps.IsDummy) return;

        var texEnvs = savedProps["m_TexEnvs.Array"];
        if (texEnvs == null || texEnvs.IsDummy) return;

        // First pass: look for standard texture property names
        foreach (var texEnv in texEnvs)
        {
            if (texEnv == null) continue;

            var firstField = texEnv["first"];
            if (firstField == null || firstField.IsDummy) continue;

            var texName = firstField.AsString;
            // Check for common texture property names across different shaders
            if (texName == "_MainTex" || texName == "_BaseMap" || texName == "_Albedo" ||
                texName == "_BaseColorMap" || texName == "_Diffuse" || texName == "_DiffuseMap" ||
                texName == "_BaseColor" || texName == "_Color" || texName == "_AlbedoMap" ||
                texName == "_MainTexture" || texName == "_Texture")
            {
                var secondField = texEnv["second"];
                if (secondField == null || secondField.IsDummy) continue;

                var texPtr = secondField["m_Texture"];
                if (texPtr == null || texPtr.IsDummy) continue;

                var texPathId = texPtr["m_PathID"].AsLong;
                var texFileId = texPtr["m_FileID"].AsInt;

                if (texPathId != 0)
                {
                    LoadTexture(fileInst, texFileId, texPathId, sceneObj);
                    return;
                }
            }
        }

        // Second pass: fallback - load any texture that exists in the material
        foreach (var texEnv in texEnvs)
        {
            if (texEnv == null) continue;

            var secondField = texEnv["second"];
            if (secondField == null || secondField.IsDummy) continue;

            var texPtr = secondField["m_Texture"];
            if (texPtr == null || texPtr.IsDummy) continue;

            var texPathId = texPtr["m_PathID"].AsLong;
            var texFileId = texPtr["m_FileID"].AsInt;

            if (texPathId != 0)
            {
                LoadTexture(fileInst, texFileId, texPathId, sceneObj);
                return;
            }
        }
    }

    private void LoadTexture(AssetsFileInstance fileInst, int texFileId, long texPathId, SceneObject sceneObj)
    {
        var texAsset = _workspace.GetAssetInst(fileInst, texFileId, texPathId);
        if (texAsset == null)
        {
            Log($"LoadTexture: texAsset is null for pathId {texPathId}");
            return;
        }

        var texBf = _workspace.GetBaseField(texAsset);
        if (texBf == null)
        {
            Log($"LoadTexture: texBf is null for pathId {texPathId}");
            return;
        }

        try
        {
            var texture = TextureFile.ReadTextureFile(texBf);

            // Validate texture properties before attempting decode
            if (texture == null)
            {
                Log($"LoadTexture: Failed to read texture file for '{sceneObj.Name}'");
                return;
            }

            if (texture.m_Width <= 0 || texture.m_Height <= 0)
            {
                Log($"LoadTexture: Invalid texture dimensions for '{sceneObj.Name}': {texture.m_Width}x{texture.m_Height}");
                return;
            }

            // Skip unsupported or problematic texture formats
            if (texture.m_TextureFormat <= 0)
            {
                Log($"LoadTexture: Unsupported texture format for '{sceneObj.Name}'");
                return;
            }

            var encData = texture.FillPictureData(texAsset.FileInstance);

            if (encData == null || encData.Length == 0)
            {
                Log($"LoadTexture: No encoded data for texture {texture.m_Name}");
                return;
            }

            // Try to decode - some formats may not be supported
            byte[]? decData = null;
            try
            {
                decData = texture.DecodeTextureRaw(encData);
            }
            catch (Exception decodeEx)
            {
                Log($"LoadTexture: Decode failed for '{texture.m_Name}' (format {texture.m_TextureFormat}): {decodeEx.Message}");
                return;
            }

            if (decData != null && decData.Length > 0)
            {
                sceneObj.TextureData = decData;
                sceneObj.TextureWidth = texture.m_Width;
                sceneObj.TextureHeight = texture.m_Height;
                Log($"Loaded texture '{texture.m_Name}' ({texture.m_Width}x{texture.m_Height}) for '{sceneObj.Name}'");
            }
            else
            {
                Log($"LoadTexture: DecodeTextureRaw returned null/empty for {texture.m_Name}");
            }
        }
        catch (Exception ex)
        {
            Log($"LoadTexture: Exception for '{sceneObj.Name}': {ex.Message}");
        }
    }

    private static Vector3 ReadVector3(AssetTypeValueField field)
    {
        return new Vector3(
            field["x"].AsFloat,
            field["y"].AsFloat,
            field["z"].AsFloat
        );
    }

    private static Quaternion ReadQuaternion(AssetTypeValueField field)
    {
        return new Quaternion(
            field["x"].AsFloat,
            field["y"].AsFloat,
            field["z"].AsFloat,
            field["w"].AsFloat
        );
    }

    public SceneObject? PickObject(Vector3 rayOrigin, Vector3 rayDirection)
    {
        SceneObject? closest = null;
        float closestDist = float.MaxValue;

        foreach (var obj in AllObjects)
        {
            if (obj.RayIntersects(rayOrigin, rayDirection, out float dist))
            {
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = obj;
                }
            }
        }

        return closest;
    }

    public void DeselectAll()
    {
        foreach (var obj in AllObjects)
        {
            obj.IsSelected = false;
        }
    }
}

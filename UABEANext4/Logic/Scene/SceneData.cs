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

        // Third pass: Load meshes and materials for objects with MeshFilter/MeshCollider
        Log("Loading meshes and materials...");
        var meshFilterInfos = fileInst.file.GetAssetsOfType(AssetClassID.MeshFilter).ToList();
        var meshRendererInfos = fileInst.file.GetAssetsOfType(AssetClassID.MeshRenderer).ToList();
        var meshColliderInfos = fileInst.file.GetAssetsOfType(AssetClassID.MeshCollider).ToList();

        Log($"Found {meshFilterInfos.Count} MeshFilters, {meshRendererInfos.Count} MeshRenderers, {meshColliderInfos.Count} MeshColliders");

        // Map GameObject PathId to MeshFilter/MeshRenderer/MeshCollider
        var goToMeshFilter = new Dictionary<long, AssetFileInfo>();
        var goToMeshRenderer = new Dictionary<long, AssetFileInfo>();
        var goToMeshCollider = new Dictionary<long, AssetFileInfo>();

        // Track static batch info for MeshRenderers (used for combined meshes)
        var goToStaticBatchInfo = new Dictionary<long, (int firstSubMesh, int subMeshCount, long batchRootPathId)>();

        // Track static flags for GameObjects (used to prefer MeshCollider mesh for static objects)
        var goStaticFlags = new Dictionary<long, uint>();
        foreach (var goInfo in goInfos)
        {
            var goBf = _workspace.GetBaseField(fileInst, goInfo.PathId);
            if (goBf != null)
            {
                // m_StaticEditorFlags: non-zero means the object has some static flags set
                var staticFlags = goBf["m_StaticEditorFlags"].AsUInt;
                goStaticFlags[goInfo.PathId] = staticFlags;
            }
        }

        // Cache for combined meshes (keyed by StaticBatchRoot PathId)
        var combinedMeshCache = new Dictionary<long, MeshObj?>();

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

            // Read static batch info for combined meshes
            var staticBatchInfo = mrBf["m_StaticBatchInfo"];
            if (staticBatchInfo != null && !staticBatchInfo.IsDummy)
            {
                var firstSubMesh = staticBatchInfo["firstSubMesh"].AsInt;
                var subMeshCount = staticBatchInfo["subMeshCount"].AsInt;

                if (subMeshCount > 0)
                {
                    // Get the static batch root (which has the combined mesh)
                    var staticBatchRoot = mrBf["m_StaticBatchRoot"];
                    long batchRootPathId = 0;
                    if (staticBatchRoot != null && !staticBatchRoot.IsDummy)
                    {
                        batchRootPathId = staticBatchRoot["m_PathID"].AsLong;
                    }

                    goToStaticBatchInfo[goPathId] = (firstSubMesh, subMeshCount, batchRootPathId);
                    Log($"Found static batch info for GO {goPathId}: firstSubMesh={firstSubMesh}, subMeshCount={subMeshCount}, batchRoot={batchRootPathId}");
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

        // Load mesh data for each scene object
        // For static objects, prefer MeshCollider's mesh (if available) over MeshFilter's mesh
        // This is important because static objects often use simplified collision meshes for accurate collision representation
        int meshesLoaded = 0;
        int meshLoadErrors = 0;
        int batchedMeshesLoaded = 0;
        foreach (var sceneObj in AllObjects)
        {
            var goPathId = tfmToGoMap.GetValueOrDefault(sceneObj.PathId, 0);
            if (goPathId == 0) continue;

            // Check if this object is static (has any static flags set)
            var isStatic = goStaticFlags.TryGetValue(goPathId, out var staticFlags) && staticFlags != 0;

            // Check if this object has a batched mesh (combined with other static objects)
            bool meshLoaded = false;
            if (goToStaticBatchInfo.TryGetValue(goPathId, out var batchInfo) && batchInfo.batchRootPathId != 0)
            {
                try
                {
                    // Try to get or load the combined mesh from the batch root
                    if (!combinedMeshCache.TryGetValue(batchInfo.batchRootPathId, out var combinedMesh))
                    {
                        // Load the combined mesh from the batch root's MeshFilter
                        // First, find the GameObject for the batch root (it's a Transform reference)
                        var batchRootTfmBf = _workspace.GetBaseField(fileInst, 0, batchInfo.batchRootPathId);
                        if (batchRootTfmBf != null)
                        {
                            var batchRootGoPathId = batchRootTfmBf["m_GameObject"]["m_PathID"].AsLong;
                            if (goToMeshFilter.TryGetValue(batchRootGoPathId, out var batchRootMfInfo))
                            {
                                var batchRootMfBf = _workspace.GetBaseField(fileInst, batchRootMfInfo.PathId);
                                if (batchRootMfBf != null)
                                {
                                    var batchMeshPtr = batchRootMfBf["m_Mesh"];
                                    if (batchMeshPtr != null && !batchMeshPtr.IsDummy)
                                    {
                                        var batchMeshPathId = batchMeshPtr["m_PathID"].AsLong;
                                        var batchMeshFileId = batchMeshPtr["m_FileID"].AsInt;

                                        if (batchMeshPathId != 0)
                                        {
                                            var batchMeshBf = _workspace.GetBaseField(fileInst, batchMeshFileId, batchMeshPathId);
                                            if (batchMeshBf != null)
                                            {
                                                var version = fileInst.file.Metadata.UnityVersion;
                                                combinedMesh = new MeshObj(fileInst, batchMeshBf, new UnityVersion(version));
                                                Log($"Loaded combined mesh from batch root (PathId: {batchInfo.batchRootPathId}) with {combinedMesh.SubMeshes?.Count ?? 0} submeshes");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        combinedMeshCache[batchInfo.batchRootPathId] = combinedMesh;
                    }

                    // Extract the submesh for this object
                    if (combinedMesh != null)
                    {
                        sceneObj.Mesh = combinedMesh.ExtractSubMesh(batchInfo.firstSubMesh, batchInfo.subMeshCount);
                        if (sceneObj.Mesh?.UVs != null && sceneObj.Mesh.UVs.Length > 0 && sceneObj.Mesh.UVs[0] != null)
                        {
                            sceneObj.UVs = sceneObj.Mesh.UVs[0];
                        }
                        meshLoaded = true;
                        batchedMeshesLoaded++;
                        meshesLoaded++;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Batched mesh extraction failed for '{sceneObj.Name}': {ex.Message}");
                    meshLoadErrors++;
                }
            }

            // For static objects without batching, try to use MeshCollider's mesh first
            if (!meshLoaded && isStatic && goToMeshCollider.TryGetValue(goPathId, out var mcInfo))
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

                                    // Get UVs if available (though collision meshes typically don't have UVs)
                                    if (sceneObj.Mesh?.UVs != null && sceneObj.Mesh.UVs.Length > 0 && sceneObj.Mesh.UVs[0] != null)
                                    {
                                        sceneObj.UVs = sceneObj.Mesh.UVs[0];
                                    }
                                    meshLoaded = true;
                                    meshesLoaded++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"MeshCollider mesh load failed for '{sceneObj.Name}': {ex.Message}");
                                meshLoadErrors++;
                                // MeshCollider mesh loading failed, will fall back to MeshFilter
                            }
                        }
                    }
                }
            }

            // Fall back to MeshFilter's mesh for non-static objects or if MeshCollider loading failed
            if (!meshLoaded && goToMeshFilter.TryGetValue(goPathId, out var mfInfo))
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

                                    // Get UVs if available
                                    if (sceneObj.Mesh?.UVs != null && sceneObj.Mesh.UVs.Length > 0 && sceneObj.Mesh.UVs[0] != null)
                                    {
                                        sceneObj.UVs = sceneObj.Mesh.UVs[0];
                                    }
                                    meshesLoaded++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"MeshFilter mesh load failed for '{sceneObj.Name}': {ex.Message}");
                                meshLoadErrors++;
                                // Mesh loading failed, skip
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

        Log($"Loaded {meshesLoaded} meshes ({batchedMeshesLoaded} from combined batches, {meshLoadErrors} errors)");

        // Compute world matrices and bounds
        Log("Computing world matrices and bounds...");
        foreach (var root in RootObjects)
        {
            root.ComputeWorldMatrix();
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

        foreach (var texEnv in texEnvs)
        {
            if (texEnv == null) continue;

            var firstField = texEnv["first"];
            if (firstField == null || firstField.IsDummy) continue;

            var texName = firstField.AsString;
            if (texName == "_MainTex" || texName == "_BaseMap" || texName == "_Albedo")
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
    }

    private void LoadTexture(AssetsFileInstance fileInst, int texFileId, long texPathId, SceneObject sceneObj)
    {
        var texAsset = _workspace.GetAssetInst(fileInst, texFileId, texPathId);
        if (texAsset == null) return;

        var texBf = _workspace.GetBaseField(texAsset);
        if (texBf == null) return;

        try
        {
            var texture = TextureFile.ReadTextureFile(texBf);
            var encData = texture.FillPictureData(texAsset.FileInstance);
            var decData = texture.DecodeTextureRaw(encData);

            if (decData != null)
            {
                sceneObj.TextureData = decData;
                sceneObj.TextureWidth = texture.m_Width;
                sceneObj.TextureHeight = texture.m_Height;
            }
        }
        catch
        {
            // Texture decode failed
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

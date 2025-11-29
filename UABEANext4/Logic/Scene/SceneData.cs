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

    public SceneData(Workspace workspace)
    {
        _workspace = workspace;
    }

    public void LoadFromFile(AssetsFileInstance fileInst)
    {
        RootObjects.Clear();
        AllObjects.Clear();
        _pathIdToObject.Clear();

        // First pass: Create all scene objects with transforms
        var transformInfos = fileInst.file.GetAssetsOfType(AssetClassID.Transform)
            .Concat(fileInst.file.GetAssetsOfType(AssetClassID.RectTransform))
            .ToList();

        var goInfos = fileInst.file.GetAssetsOfType(AssetClassID.GameObject).ToList();
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

        // Third pass: Load meshes and materials for objects with MeshFilter or MeshCollider
        var meshFilterInfos = fileInst.file.GetAssetsOfType(AssetClassID.MeshFilter).ToList();
        var meshRendererInfos = fileInst.file.GetAssetsOfType(AssetClassID.MeshRenderer).ToList();
        var meshColliderInfos = fileInst.file.GetAssetsOfType(AssetClassID.MeshCollider).ToList();

        // Map GameObject PathId to MeshFilter/MeshRenderer/MeshCollider
        var goToMeshFilter = new Dictionary<long, AssetFileInfo>();
        var goToMeshRenderer = new Dictionary<long, AssetFileInfo>();
        var goToMeshCollider = new Dictionary<long, AssetFileInfo>();

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
        }

        foreach (var mcInfo in meshColliderInfos)
        {
            var mcBf = _workspace.GetBaseField(fileInst, mcInfo.PathId);
            if (mcBf == null) continue;

            var goPathId = mcBf["m_GameObject"]["m_PathID"].AsLong;
            goToMeshCollider[goPathId] = mcInfo;
        }

        // Load mesh data for each scene object
        foreach (var sceneObj in AllObjects)
        {
            var goPathId = tfmToGoMap.GetValueOrDefault(sceneObj.PathId, 0);
            if (goPathId == 0) continue;

            // Try to load mesh - prefer MeshCollider mesh if available, fallback to MeshFilter
            bool meshLoaded = false;

            // First try MeshCollider - these often have cleaner meshes for rendering
            if (goToMeshCollider.TryGetValue(goPathId, out var mcInfo))
            {
                meshLoaded = TryLoadMeshFromCollider(fileInst, mcInfo.PathId, sceneObj);
            }

            // If no MeshCollider mesh, try MeshFilter
            if (!meshLoaded && goToMeshFilter.TryGetValue(goPathId, out var mfInfo))
            {
                meshLoaded = TryLoadMeshFromFilter(fileInst, mfInfo.PathId, sceneObj);
            }

            // Load texture from material (via MeshRenderer)
            if (goToMeshRenderer.TryGetValue(goPathId, out var mrInfo))
            {
                TryLoadTextureFromRenderer(fileInst, mrInfo.PathId, sceneObj);
            }
        }

        // Compute world matrices and bounds
        foreach (var root in RootObjects)
        {
            root.ComputeWorldMatrix();
        }

        foreach (var obj in AllObjects)
        {
            obj.ComputeBounds();
        }
    }

    private bool TryLoadMeshFromCollider(AssetsFileInstance fileInst, long colliderPathId, SceneObject sceneObj)
    {
        try
        {
            var mcBf = _workspace.GetBaseField(fileInst, colliderPathId);
            if (mcBf == null) return false;

            var meshPtr = mcBf["m_Mesh"];
            var meshPathId = meshPtr["m_PathID"].AsLong;
            var meshFileId = meshPtr["m_FileID"].AsInt;

            if (meshPathId == 0) return false;

            var meshBf = _workspace.GetBaseField(fileInst, meshFileId, meshPathId);
            if (meshBf == null) return false;

            var version = fileInst.file.Metadata.UnityVersion;
            sceneObj.Mesh = new MeshObj(fileInst, meshBf, new UnityVersion(version));

            // Get UVs if available
            if (sceneObj.Mesh.UVs != null && sceneObj.Mesh.UVs.Length > 0 && sceneObj.Mesh.UVs[0] != null)
            {
                sceneObj.UVs = sceneObj.Mesh.UVs[0];
            }

            return sceneObj.HasMesh;
        }
        catch
        {
            return false;
        }
    }

    private bool TryLoadMeshFromFilter(AssetsFileInstance fileInst, long filterPathId, SceneObject sceneObj)
    {
        try
        {
            var mfBf = _workspace.GetBaseField(fileInst, filterPathId);
            if (mfBf == null) return false;

            var meshPtr = mfBf["m_Mesh"];
            var meshPathId = meshPtr["m_PathID"].AsLong;
            var meshFileId = meshPtr["m_FileID"].AsInt;

            if (meshPathId == 0) return false;

            var meshBf = _workspace.GetBaseField(fileInst, meshFileId, meshPathId);
            if (meshBf == null) return false;

            var version = fileInst.file.Metadata.UnityVersion;
            sceneObj.Mesh = new MeshObj(fileInst, meshBf, new UnityVersion(version));

            // Get UVs if available
            if (sceneObj.Mesh.UVs != null && sceneObj.Mesh.UVs.Length > 0 && sceneObj.Mesh.UVs[0] != null)
            {
                sceneObj.UVs = sceneObj.Mesh.UVs[0];
            }

            return sceneObj.HasMesh;
        }
        catch
        {
            return false;
        }
    }

    private void TryLoadTextureFromRenderer(AssetsFileInstance fileInst, long rendererPathId, SceneObject sceneObj)
    {
        try
        {
            var mrBf = _workspace.GetBaseField(fileInst, rendererPathId);
            if (mrBf == null) return;

            var materialsArr = mrBf["m_Materials.Array"];
            if (materialsArr.Children.Count == 0) return;

            // Try each material until we find a texture
            foreach (var matPtr in materialsArr)
            {
                var matPathId = matPtr["m_PathID"].AsLong;
                var matFileId = matPtr["m_FileID"].AsInt;

                if (matPathId != 0)
                {
                    LoadTextureFromMaterial(fileInst, matFileId, matPathId, sceneObj);
                    if (sceneObj.HasTexture)
                    {
                        return; // Found a texture, stop looking
                    }
                }
            }
        }
        catch
        {
            // Texture loading failed
        }
    }

    // Common texture property names used by various Unity shaders
    private static readonly string[] TexturePropertyNames = new[]
    {
        "_MainTex",      // Standard shader
        "_BaseMap",      // URP/HDRP Lit shader
        "_Albedo",       // Some custom shaders
        "_BaseColorMap", // HDRP
        "_Diffuse",      // Legacy shaders
        "_DiffuseMap",   // Some custom shaders
        "mainTexture",   // Alternative naming
        "_Texture",      // Generic
    };

    private void LoadTextureFromMaterial(AssetsFileInstance fileInst, int matFileId, long matPathId, SceneObject sceneObj)
    {
        var matBf = _workspace.GetBaseField(fileInst, matFileId, matPathId);
        if (matBf == null) return;

        var texEnvs = matBf["m_SavedProperties"]["m_TexEnvs.Array"];
        if (texEnvs.IsDummy) return;

        // First pass: look for known texture property names
        foreach (var texName in TexturePropertyNames)
        {
            foreach (var texEnv in texEnvs)
            {
                if (texEnv["first"].AsString == texName)
                {
                    var texPtr = texEnv["second"]["m_Texture"];
                    var texPathId = texPtr["m_PathID"].AsLong;
                    var texFileId = texPtr["m_FileID"].AsInt;

                    if (texPathId != 0)
                    {
                        LoadTexture(fileInst, texFileId, texPathId, sceneObj);
                        if (sceneObj.HasTexture) return;
                    }
                }
            }
        }

        // Second pass: try any texture that has a valid reference
        foreach (var texEnv in texEnvs)
        {
            var texPtr = texEnv["second"]["m_Texture"];
            var texPathId = texPtr["m_PathID"].AsLong;
            var texFileId = texPtr["m_FileID"].AsInt;

            if (texPathId != 0)
            {
                LoadTexture(fileInst, texFileId, texPathId, sceneObj);
                if (sceneObj.HasTexture) return;
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

            if (decData != null && decData.Length > 0)
            {
                // Flip texture vertically for OpenGL (Unity has top-left origin, OpenGL has bottom-left)
                var flippedData = FlipTextureVertically(decData, texture.m_Width, texture.m_Height);

                sceneObj.TextureData = flippedData;
                sceneObj.TextureWidth = texture.m_Width;
                sceneObj.TextureHeight = texture.m_Height;
            }
        }
        catch
        {
            // Texture decode failed
        }
    }

    private static byte[] FlipTextureVertically(byte[] data, int width, int height)
    {
        // Assuming BGRA format (4 bytes per pixel)
        int bytesPerPixel = 4;
        int stride = width * bytesPerPixel;
        var flipped = new byte[data.Length];

        for (int y = 0; y < height; y++)
        {
            int srcOffset = y * stride;
            int dstOffset = (height - 1 - y) * stride;
            Buffer.BlockCopy(data, srcOffset, flipped, dstOffset, stride);
        }

        return flipped;
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

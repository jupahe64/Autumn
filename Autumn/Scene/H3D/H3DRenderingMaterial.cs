using System.Collections;
using System.Diagnostics;
using System.Numerics;
using Autumn.Storage;
using Autumn.Utils;
using SceneGL;
using SceneGL.GLHelpers;
using SceneGL.GLWrappers;
using SceneGL.Materials.Common;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using SPICA.Formats.CtrGfx.Model.Material;
using SPICA.Formats.CtrH3D.Model.Material;
using SPICA.Formats.CtrH3D.Model.Mesh;
using SPICA.PICA.Commands;

namespace Autumn.Scene.H3D;

internal class H3DRenderingMaterial
{
    private struct SceneData
    {
        public Matrix3X4<float> WrldMtx;
        public Matrix3X4<float> NormMtx;
        public Vector4 PosOffset;
        public Matrix2X4<float> IrScale;
        public Vector4 TexcMap;
        public Matrix3X4<float> TexMtx0;
        public Matrix3X4<float> TexMtx1;
        public Matrix2X4<float> TexMtx2;
        public Vector4 TexTran;
        public Vector4 MatAmbi;
        public Vector4 MatDiff;
        public Vector4 HslGCol;
        public Vector4 HslSCol;
        public Vector4 HslSDir;
        public Matrix4x4 ProjMtx;
        public Matrix3X4<float> ViewMtx;
        public Vector4D<int> LightCt;
        public Bools BoolUniforms;
        public Silk.NET.OpenGL.Boolean DisableVertexColor;
        public Vector2 _Padding;

        [Flags]
        public enum Bools : int
        {
            IsSmoSk = 1 << 1,
            IsRgdSk = 1 << 2,
            IsHemiL = 1 << 5,
            IsHemiO = 1 << 6,
            IsVertA = 1 << 7,
            IsBoneW = 1 << 8,
            UvMap0 = 1 << 9,
            UvMap1 = 1 << 10,
            UvMap2 = 1 << 11,
            IsVertL = 1 << 12,
            IsTex1 = 1 << 13,
            IsTex2 = 1 << 14,
            IsQuate = 1 << 15
        }
    }

    private struct MaterialData
    {
        public int LightsCount;
        public Vector3 _Padding0;
        public Light Light0;
        public Light Light1;
        public Light Light2;
        public Vector4 SAmbient;
        public Vector4 EmissionColor;
        public Vector4 AmbientColor;
        public Vector4 DiffuseColor;
        public Vector4 Specular0Color;
        public Vector4 Specular1Color;
        public Vector4 Constant0Color;
        public Vector4 Constant1Color;
        public Vector4 Constant2Color;
        public Vector4 Constant3Color;
        public Vector4 Constant4Color;
        public Vector4 Constant5Color;
        public Vector4 CombBufferColor;
        public float AlphaReference;
        public Vector3 _Padding1;
        public Vector4 SelectionColor;

        public struct Light
        {
            public Vector3 Position;
            public int _Padding0;
            public Vector3 Direction;
            public int _Padding1;
            public Vector4 Ambient;
            public Vector4 Diffuse;
            public Vector4 Specular0;
            public Vector4 Specular1;
            public float AttenuationScale;
            public float AttenuationBias;
            public float AngleLUTScale;
            public int AngleLUTInput;
            public Silk.NET.OpenGL.Boolean SpotAttEnbled;
            public Silk.NET.OpenGL.Boolean DistAttEnbled;
            public Silk.NET.OpenGL.Boolean TwoSidedDiffuse;
            public Silk.NET.OpenGL.Boolean Directional;
        }
    }

    private readonly ShaderProgram _program;

    private readonly ShaderParams _sceneParams;
    private readonly ShaderParams _materialParams;

    private readonly UniformBuffer<SceneData> _sceneBuffer;
    private readonly UniformBuffer<MaterialData> _materialBuffer;

    private readonly UniformArrayBuffer<Matrix3X4<float>> _univRegBuffer;
    private readonly UniformArrayBuffer<Vector4D<int>> _boneTableBuffer;

    private Matrix4x4 _lastTransform;

    public CullFaceMode CullFaceMode { get; }
    public H3DRenderingLayer Layer { get; }

    public bool BlendingEnabled { get; }

    public Vector4 BlendingColor { get; }

    public BlendEquationModeEXT ColorBlendEquation { get; }
    public BlendEquationModeEXT AlphaBlendEquation { get; }

    public BlendingFactor ColorSrcFact { get; }
    public BlendingFactor ColorDstFact { get; }

    public BlendingFactor AlphaSrcFact { get; }
    public BlendingFactor AlphaDstFact { get; }

    public ShaderProgram Program => _program;

    private static readonly Dictionary<string, ShaderProgram> s_shaderCache = new();

    public H3DRenderingMaterial(GL gl, H3DMaterial material, H3DMesh mesh, ActorObj actorObj)
    {
        H3DMaterialParams matParams = material.MaterialParams;
        H3DSubMesh subMesh = mesh.SubMeshes[0];

        Debug.Assert(mesh.SubMeshes.Count == 1);

        ShaderSource vertexShader = H3DShaders.VertexShader;
        ShaderSource fragmentShader = H3DShaders.GetFragmentShader(
            material.Name,
            material.MaterialParams
        );

        Layer = (H3DRenderingLayer)mesh.Layer;

        // Blending:
        BlendingEnabled = matParams.BlendMode != GfxFragOpBlendMode.None;

        BlendingColor = new(
            matParams.BlendColor.R / 255f,
            matParams.BlendColor.G / 255f,
            matParams.BlendColor.B / 255f,
            matParams.BlendColor.A / 255f
        );

        ColorBlendEquation = FromPICABlendEquation(matParams.BlendFunction.ColorEquation);
        AlphaBlendEquation = FromPICABlendEquation(matParams.BlendFunction.AlphaEquation);

        ColorSrcFact = FromPICABlendFunc(matParams.BlendFunction.ColorSrcFunc);
        ColorDstFact = FromPICABlendFunc(matParams.BlendFunction.ColorDstFunc);
        AlphaSrcFact = FromPICABlendFunc(matParams.BlendFunction.AlphaSrcFunc);
        AlphaDstFact = FromPICABlendFunc(matParams.BlendFunction.AlphaDstFunc);

        BlendEquationModeEXT FromPICABlendEquation(PICABlendEquation equation) =>
            equation switch
            {
                PICABlendEquation.FuncSubtract => BlendEquationModeEXT.FuncSubtract,
                PICABlendEquation.FuncReverseSubtract => BlendEquationModeEXT.FuncReverseSubtract,
                PICABlendEquation.Min => BlendEquationModeEXT.Min,
                PICABlendEquation.Max => BlendEquationModeEXT.Max,
                _ => BlendEquationModeEXT.FuncAdd
            };

        BlendingFactor FromPICABlendFunc(PICABlendFunc func) =>
            func switch
            {
                PICABlendFunc.Zero => BlendingFactor.Zero,
                PICABlendFunc.One => BlendingFactor.One,
                PICABlendFunc.SourceColor => BlendingFactor.SrcColor,
                PICABlendFunc.OneMinusSourceColor => BlendingFactor.OneMinusSrcColor,
                PICABlendFunc.DestinationColor => BlendingFactor.DstColor,
                PICABlendFunc.OneMinusDestinationColor => BlendingFactor.OneMinusDstColor,
                PICABlendFunc.SourceAlpha => BlendingFactor.SrcAlpha,
                PICABlendFunc.OneMinusSourceAlpha => BlendingFactor.OneMinusSrcAlpha,
                PICABlendFunc.DestinationAlpha => BlendingFactor.DstAlpha,
                PICABlendFunc.OneMinusDestinationAlpha => BlendingFactor.OneMinusDstAlpha,
                PICABlendFunc.ConstantColor => BlendingFactor.ConstantColor,
                PICABlendFunc.OneMinusConstantColor => BlendingFactor.OneMinusConstantColor,
                PICABlendFunc.ConstantAlpha => BlendingFactor.ConstantAlpha,
                PICABlendFunc.OneMinusConstantAlpha => BlendingFactor.OneMinusConstantAlpha,
                PICABlendFunc.SourceAlphaSaturate => BlendingFactor.SrcAlphaSaturate
            };

        if (!s_shaderCache.TryGetValue(fragmentShader.Code, out _program!))
        {
            _program = new(vertexShader, fragmentShader)
            {
                FragShaderOutputBindings = new (string, uint)[] { ("Output", 0), ("oPickingId", 1) }
            };

            s_shaderCache[fragmentShader.Code] = _program;
        }

        CullFaceMode = matParams.FaceCulling switch
        {
            PICAFaceCulling.FrontFace => CullFaceMode.Front,
            PICAFaceCulling.BackFace => CullFaceMode.Back,
            _ => 0
        };

        // SceneData -----------------------------------------------------------------------------------

        SceneData sceneData =
            new()
            {
                WrldMtx = Matrix3X4<float>.Identity,
                PosOffset = mesh.PositionOffset,
                TexcMap = new(matParams.TextureSources),
                HslGCol = new(0.56471f, 0.34118f, 0.03529f, 0.00f),
                HslSCol = new(0.46275f, 0.76078f, 0.87059f, 0.00f),
                HslSDir = new(0.0f, 0.95703f, 0.28998f, 0.40f),
                BoolUniforms = (SceneData.Bools)subMesh.BoolUniforms
            };

        // Scales:
        foreach (PICAAttribute attribute in mesh.Attributes)
            switch (attribute.Name)
            {
                case PICAAttributeName.Position:
                    sceneData.IrScale.Row1.X = attribute.Scale;
                    break;
                case PICAAttributeName.Normal:
                    sceneData.IrScale.Row1.Y = attribute.Scale;
                    break;
                case PICAAttributeName.Tangent:
                    sceneData.IrScale.Row1.Z = attribute.Scale;
                    break;
                case PICAAttributeName.Color:
                    sceneData.IrScale.Row1.W = attribute.Scale;
                    break;
                case PICAAttributeName.TexCoord0:
                    sceneData.IrScale.Row2.X = attribute.Scale;
                    break;
                case PICAAttributeName.TexCoord1:
                    sceneData.IrScale.Row2.Y = attribute.Scale;
                    break;
                case PICAAttributeName.TexCoord2:
                    sceneData.IrScale.Row2.Z = attribute.Scale;
                    break;
                case PICAAttributeName.BoneWeight:
                    sceneData.IrScale.Row2.W = attribute.Scale;
                    break;
            }

        // Texture transforms:
        for (byte i = 0; i < 3; i++)
        {
            H3DTextureCoord coord = matParams.TextureCoords[i];

            Matrix2X4<float> transform = MathUtils.GetTextureTransform(
                coord.Scale,
                coord.Rotation,
                coord.Translation,
                coord.TransformType
            );

            switch (i)
            {
                case 0:
                    sceneData.TexMtx0 = new(transform) { M33 = 1 };
                    break;
                case 1:
                    sceneData.TexMtx1 = new(transform) { M33 = 1 };
                    break;
                case 2:
                    sceneData.TexMtx2 = transform;
                    break;
            }
        }

        sceneData.TexTran = new(
            sceneData.TexMtx0.Row3.X,
            sceneData.TexMtx0.Row3.Y,
            sceneData.TexMtx1.Row3.X,
            sceneData.TexMtx1.Row3.Y
        );

        sceneData.MatAmbi = new(
            matParams.AmbientColor.R / 255f,
            matParams.AmbientColor.G / 255f,
            matParams.AmbientColor.B / 255f,
            matParams.ColorScale
        );

        sceneData.MatDiff = new Vector4(
            matParams.DiffuseColor.R / 255f,
            matParams.DiffuseColor.G / 255f,
            matParams.DiffuseColor.B / 255f,
            1f
        );

        // MaterialData --------------------------------------------------------------------------------

        MaterialData materialData =
            new()
            {
                LightsCount = 1,
                EmissionColor = matParams.EmissionColor.ToVector4(),
                AmbientColor = matParams.AmbientColor.ToVector4(),
                DiffuseColor = matParams.DiffuseColor.ToVector4(),
                Specular0Color = matParams.Specular0Color.ToVector4(),
                Specular1Color = matParams.Specular1Color.ToVector4(),
                Constant0Color = matParams.Constant0Color.ToVector4(),
                Constant1Color = matParams.Constant1Color.ToVector4(),
                Constant2Color = matParams.Constant2Color.ToVector4(),
                Constant3Color = matParams.Constant3Color.ToVector4(),
                Constant4Color = matParams.Constant4Color.ToVector4(),
                Constant5Color = matParams.Constant5Color.ToVector4(),
                AlphaReference = matParams.AlphaTest.Reference / 225f,
                Light0 = new()
                {
                    Ambient = new(0.1f, 0.1f, 0.1f, 1),
                    Diffuse = new(0.4f, 0.4f, 0.4f, 1),
                    Specular0 = new(0.8f, 0.8f, 0.8f, 1),
                    Specular1 = new(0.4f, 0.4f, 0.4f, 1),
                    TwoSidedDiffuse = Silk.NET.OpenGL.Boolean.True
                }
            };

        // Others (UnivReg and BoneTable) --------------------------------------------------------------

        Matrix3X4<float>[] packedTransforms = new Matrix3X4<float>[20];
        Vector4D<int>[] boneTable = new Vector4D<int>[20];

        if (actorObj.SkeletalAnimatior is not null)
        {
            Matrix4x4[] transforms = actorObj.SkeletalAnimatior.GetSkeletonTransforms();

            for (int i = 0; i < packedTransforms.Length; i++)
            {
                packedTransforms[i] = Matrix3X4<float>.Identity;

                if (i < subMesh.BoneIndices.Length)
                {
                    ushort boneIndex = subMesh.BoneIndices[i];

                    if (boneIndex < transforms.Length)
                    {
                        boneTable[i].X = boneIndex;

                        if (subMesh.Skinning != H3DSubMeshSkinning.Smooth)
                            MathUtils.Pack3dTransformMatrix(
                                in transforms[boneIndex],
                                ref packedTransforms[i]
                            );
                    }
                }
            }
        }
        else
            for (int i = 0; i < packedTransforms.Length; i++)
                packedTransforms[i] = Matrix3X4<float>.Identity;

        // Textures and LUTs ---------------------------------------------------------------------------

        TextureSampler?[] textureSamplers = new TextureSampler?[10]; // 0 -> 3: Textures, 4 -> 9: LUTs

        {
            TextureSampler? textureSampler0 = CreateTextureSampler(material.Texture0Name, 0);

            if (matParams.TextureCoords[0].MappingType == H3DTextureMappingType.CameraCubeEnvMap)
                textureSamplers[3] = textureSampler0;
            else
                textureSamplers[0] = textureSampler0;
        }

        textureSamplers[1] = CreateTextureSampler(material.Texture1Name, 1);
        textureSamplers[2] = CreateTextureSampler(material.Texture2Name, 2);

        textureSamplers[4] = CreateLUTTextureSampler(
            matParams.LUTDist0TableName,
            matParams.LUTDist0SamplerName
        );

        textureSamplers[5] = CreateLUTTextureSampler(
            matParams.LUTDist1TableName,
            matParams.LUTDist1SamplerName
        );

        textureSamplers[6] = CreateLUTTextureSampler(
            matParams.LUTFresnelTableName,
            matParams.LUTFresnelSamplerName
        );

        textureSamplers[7] = CreateLUTTextureSampler(
            matParams.LUTReflecRTableName,
            matParams.LUTReflecRSamplerName
        );

        textureSamplers[8] =
            CreateLUTTextureSampler(matParams.LUTReflecGTableName, matParams.LUTReflecGSamplerName)
            ?? textureSamplers[7];

        textureSamplers[9] =
            CreateLUTTextureSampler(matParams.LUTReflecBTableName, matParams.LUTReflecBSamplerName)
            ?? textureSamplers[7];

        TextureSampler? CreateTextureSampler(string name, byte position)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            uint sampler;
            uint texture;

            ActorObj.RGBATexture tex = actorObj.RGBATextures[name];

            texture = TextureHelper.CreateTexture2D<byte>(
                gl,
                SceneGL.PixelFormat.R8_G8_B8_A8_UNorm,
                tex.Width,
                tex.Height,
                tex.Data,
                true
            );

            H3DTextureMapper mapper = material.TextureMappers[position];

            TextureWrapMode wrapModeS = FromPICATextureWrap(mapper.WrapU);
            TextureWrapMode wrapModeT = FromPICATextureWrap(mapper.WrapV);

            TextureMagFilter magFilter = mapper.MagFilter switch
            {
                H3DTextureMagFilter.Nearest => TextureMagFilter.Nearest,
                _ => TextureMagFilter.Linear
            };

            TextureMinFilter minFilter = mapper.MinFilter switch
            {
                H3DTextureMinFilter.Nearest => TextureMinFilter.Nearest,
                H3DTextureMinFilter.NearestMipmapNearest => TextureMinFilter.NearestMipmapNearest,
                H3DTextureMinFilter.NearestMipmapLinear => TextureMinFilter.NearestMipmapLinear,
                H3DTextureMinFilter.LinearMipmapNearest => TextureMinFilter.LinearMipmapNearest,
                H3DTextureMinFilter.LinearMipmapLinear => TextureMinFilter.LinearMipmapLinear,
                _ => TextureMinFilter.Linear
            };

            sampler = SamplerHelper.CreateSampler2D(gl, wrapModeS, wrapModeT, magFilter, minFilter);

            return new(sampler, texture);

            static TextureWrapMode FromPICATextureWrap(PICATextureWrap textureWrap) =>
                textureWrap switch
                {
                    PICATextureWrap.ClampToEdge => TextureWrapMode.ClampToEdge,
                    PICATextureWrap.ClampToBorder => TextureWrapMode.ClampToBorder,
                    PICATextureWrap.Mirror => TextureWrapMode.MirroredRepeat,
                    _ => TextureWrapMode.Repeat
                };
        }

        TextureSampler? CreateLUTTextureSampler(string tableName, string samplerName)
        {
            string name = tableName + "/" + samplerName;

            uint sampler;
            uint texture;

            if (!actorObj.LUTSamplerTextures.TryGetValue(name, out float[]? table))
                table = new float[512];

            texture = TextureHelper.CreateTexture2D<float>(
                gl,
                SceneGL.PixelFormat.R32_Float,
                (uint)table.Length,
                1,
                table,
                false
            );

            sampler = SamplerHelper.GetOrCreate(gl, SamplerHelper.DefaultSamplerKey.NEAREST);

            return new(sampler, texture);
        }

        // ---------------------------------------------------------------------------------------------

        _sceneBuffer = new(sceneData, "ubSceneBuffer");
        _univRegBuffer = new(packedTransforms, "UnivRefBuffer");
        _boneTableBuffer = new(boneTable, "BoneTableBuffer");

        _sceneParams = new(
            new BufferBinding[]
            {
                new("ubScene", default),
                new("ubUnivReg", _univRegBuffer.GetDataBuffer(gl)),
                new("ubBoneTable", _boneTableBuffer.GetDataBuffer(gl))
            },
            Array.Empty<SamplerBinding>()
        );

        _sceneParams.EvaluatingResources += (_gl) =>
            _sceneParams.SetBufferBinding("ubScene", _sceneBuffer.GetDataBuffer(_gl));

        List<SamplerBinding> samplerBindings = new();

        for (byte i = 0; i < 10; i++)
        {
            TextureSampler? textureSampler = textureSamplers[i];

            textureSampler ??= new(
                SamplerHelper.GetOrCreate(gl, SamplerHelper.DefaultSamplerKey.NEAREST),
                TextureHelper.GetOrCreate(gl, TextureHelper.DefaultTextureKey.BLACK)
            );

            string name;

            if (i == 3)
                name = "TextureCube";
            else
                name = i < 4 ? $"Textures[{i}]" : $"LUTs[{i - 4}]";

            samplerBindings.Add(
                new(name, textureSampler.Value.Sampler, textureSampler.Value.Texture)
            );
        }

        samplerBindings.Add(
            new(
                "UVTestPattern",
                SamplerHelper.GetOrCreate(gl, SamplerHelper.DefaultSamplerKey.NEAREST),
                TextureHelper.GetOrCreate(gl, TextureHelper.DefaultTextureKey.BLACK)
            )
        );

        _materialParams = ShaderParams.FromUniformBlockDataAndSamplers(
            "ubMaterial",
            materialData,
            "ubMaterialBuffer",
            samplerBindings.ToArray(),
            out _materialBuffer
        );
    }

    public void SetMatrices(Matrix4x4 projection, Matrix4x4 transform, Matrix4x4 view)
    {
        Matrix3X4<float> transformView = new();
        MathUtils.Pack3dTransformMatrix(transform * view, ref transformView);

        Matrix3X4<float> normMtx = new();

        if (_lastTransform != transform)
        {
            _lastTransform = transform;

            MathUtils.ClearScale(ref transform); // "transform" is modified after this line.
            MathUtils.Pack3dTransformMatrix(transform, ref normMtx);
        }
        else
            normMtx = _sceneBuffer.Data.NormMtx;

        _sceneBuffer.SetData(
            _sceneBuffer.Data with
            {
                ViewMtx = transformView,
                ProjMtx = projection,
                NormMtx = normMtx
            }
        );
    }

    public void SetSelectionColor(Vector4 color) =>
        _materialBuffer.SetData(_materialBuffer.Data with { SelectionColor = color });

    public bool TryUse(GL gl, out ProgramUniformScope scope) =>
        _program.TryUse(
            gl,
            null,
            new IShaderBindingContainer[] { _sceneParams, _materialParams },
            out scope,
            out _
        );
}

public enum H3DRenderingLayer
{
    Opaque = 0,
    Translucent = 1,
    Substractive = 2,
    Addictive = 3
}

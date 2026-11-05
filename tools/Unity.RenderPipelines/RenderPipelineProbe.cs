using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.HighDefinition;
using SocketJack.Samples.Physics;

namespace SocketJack.Tests.Rendering
{
    public sealed class ProbePipelineAsset : RenderPipelineAsset
    {
        public Material material;
        public override Material defaultMaterial => material;
        protected override RenderPipeline CreatePipeline() => new ProbePipeline();
    }
    public sealed class ProbePipeline : RenderPipeline
    {
        protected override void Render(ScriptableRenderContext context, Camera[] cameras)
        { foreach (var camera in cameras) Draw(context, camera); }
        protected override bool IsRenderRequestSupported<T>(Camera camera, T request) => request is StandardRequest;
        protected override void ProcessRenderRequests<T>(ScriptableRenderContext context, Camera camera, T request)
        {
            var standard = request as StandardRequest;
            if (standard == null) throw new NotSupportedException();
            var prior = camera.targetTexture; camera.targetTexture = standard.destination;
            try { Draw(context, camera); } finally { camera.targetTexture = prior; }
        }
        private static void Draw(ScriptableRenderContext context, Camera camera)
        {
            if (!camera.TryGetCullingParameters(out var culling)) return;
            var results = context.Cull(ref culling);
            context.SetupCameraProperties(camera);
            var command = new CommandBuffer();
            command.SetRenderTarget(camera.targetTexture == null ? (RenderTargetIdentifier)BuiltinRenderTextureType.CameraTarget : camera.targetTexture);
            command.ClearRenderTarget(true, true, camera.backgroundColor);
            context.ExecuteCommandBuffer(command); command.Release();
            var drawing = new DrawingSettings(new ShaderTagId("SRPDefaultUnlit"), new SortingSettings(camera) { criteria = SortingCriteria.CommonOpaque });
            var filtering = new FilteringSettings(RenderQueueRange.all);
            context.DrawRenderers(results, ref drawing, ref filtering);
            context.Submit();
        }
    }
    public static class RenderPipelineProbe
    {
        private const string Generated = "Assets/SocketJackPipelineValidation/Generated";
        [Serializable] private sealed class Receipt
        {
            public string result, unityVersion, pipeline, actualPipelineType, shader, graphicsDevice, graphicsApi, image, checkedUtc;
            public int width, height, coloredPixels, magentaPixels;
            public bool inspectorOverrideClonedWithoutMutatingAsset;
        }
        private static Camera camera;
        private static string pipeline, output;
        private static RenderTexture target;
        private static Material material;
        private static int frames;
        private static double deadline;
        private static bool cloned;
        private static RenderPipelineAsset originalGraphics, originalQuality;
        public static void Run()
        {
            pipeline = Argument("-socketjackPipeline"); output = Argument("-socketjackRenderOutput");
            Directory.CreateDirectory(output); Directory.CreateDirectory(Generated);
            originalGraphics = GraphicsSettings.defaultRenderPipeline; originalQuality = QualitySettings.renderPipeline;
            EditorApplication.quitting += RestorePipeline;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderPipelineAsset asset = null;
            if (pipeline == "URP")
            {
                var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, AssetDatabase.GenerateUniqueAssetPath(Generated + "/Renderer.asset"));
                asset = UniversalRenderPipelineAsset.Create(renderer);
            }
            else if (pipeline == "HDRP") asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            else if (pipeline == "CustomRP")
            {
                var custom = ScriptableObject.CreateInstance<ProbePipelineAsset>();
                custom.material = new Material(Shader.Find("SocketJack/CompatibilityProbeUnlit"));
                AssetDatabase.CreateAsset(custom.material, AssetDatabase.GenerateUniqueAssetPath(Generated + "/CustomMaterial.mat")); asset = custom;
            }
            else if (pipeline != "BuiltIn") throw new ArgumentException("Unknown pipeline " + pipeline);
            if (asset != null) AssetDatabase.CreateAsset(asset, AssetDatabase.GenerateUniqueAssetPath(Generated + "/" + pipeline + ".asset"));
            QualitySettings.renderPipeline = null; GraphicsSettings.defaultRenderPipeline = asset;
            camera = new GameObject("Pipeline acceptance camera").AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 4, -8); camera.transform.LookAt(new Vector3(0, 0.5f, 0));
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.03f, 0.04f, 0.06f);
            if (pipeline == "URP") camera.GetUniversalAdditionalCameraData();
            if (pipeline == "HDRP")
            {
                var data = camera.gameObject.AddComponent<HDAdditionalCameraData>();
                data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                data.backgroundColorHDR = camera.backgroundColor;
            }
            target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGBHalf); target.Create();
            camera.targetTexture = target;
            material = PhysicsSampleMaterialFactory.Create(new Color(0.12f, 0.68f, 0.92f));
            var before = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            var game = new GameObject("Inspector material assignment check").AddComponent<PhysicsGame>();
            game.SampleMaterial = material;
            var clone = PhysicsSampleMaterialFactory.Create(Color.red, game.SampleMaterial);
            cloned = clone != material && game.SampleMaterial == material && (!material.HasProperty("_Color") || material.GetColor("_Color") == before);
            UnityEngine.Object.DestroyImmediate(clone); UnityEngine.Object.DestroyImmediate(game.gameObject);
            for (int i = 0; i < 3; i++)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.position = new Vector3((i - 1) * 2, 0.6f, i % 2);
                cube.GetComponent<Renderer>().sharedMaterial = PhysicsSampleMaterialFactory.Create(i == 1 ? new Color(0.95f, 0.62f, 0.15f) : new Color(0.12f, 0.68f, 0.92f));
            }
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.transform.position = new Vector3(0, -0.25f, 0);
            floor.transform.localScale = new Vector3(14, 0.5f, 14); floor.GetComponent<Renderer>().sharedMaterial = PhysicsSampleMaterialFactory.Create(new Color(0.2f, 0.23f, 0.27f));
            var light = new GameObject("Sun").AddComponent<Light>(); light.type = LightType.Directional;
            light.intensity = 1.1f; light.transform.rotation = Quaternion.Euler(50, -30, 0);
            RenderSettings.ambientLight = Color.gray;
            deadline = EditorApplication.timeSinceStartup + 300; frames = 0;
            EditorApplication.update += Tick; EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll();
        }
        private static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Pipeline rendering timed out.");
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                if (frames++ < 10) { EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll(); return; }
                if (pipeline == "BuiltIn") camera.Render();
                else
                {
                    var request = new RenderPipeline.StandardRequest { destination = target };
                    if (!RenderPipeline.SupportsRenderRequest(camera, request)) throw new InvalidOperationException("Active pipeline has not initialized StandardRequest support: " + RenderPipelineManager.currentPipeline);
                    RenderPipeline.SubmitRenderRequest(camera, request);
                }
                if (frames < 15) return;
                RenderTexture.active = target;
                var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
                var pixels = image.GetPixels32();
                int colored = pixels.Count(p => (p.b > p.r * 1.25f && p.b > p.g * 1.08f && p.b > 60) || (p.r > p.b * 1.6f && p.g > p.b * 1.4f && p.r > 80));
                int magenta = pixels.Count(p => p.r > 180 && p.b > 180 && p.g < 60);
                string path = Path.Combine(output, pipeline + ".png"); File.WriteAllBytes(path, image.EncodeToPNG());
                var receipt = new Receipt { result = colored > 10000 && magenta < 100 && cloned ? "passed" : "failed-pixels", unityVersion = Application.unityVersion,
                    pipeline = pipeline, actualPipelineType = RenderPipelineManager.currentPipeline == null ? "Built-in" : RenderPipelineManager.currentPipeline.GetType().FullName,
                    shader = material.shader.name, graphicsDevice = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(), image = path,
                    width = target.width, height = target.height, coloredPixels = colored, magentaPixels = magenta, inspectorOverrideClonedWithoutMutatingAsset = cloned, checkedUtc = DateTime.UtcNow.ToString("O") };
                File.WriteAllText(Path.Combine(output, pipeline + ".json"), JsonUtility.ToJson(receipt, true));
                Debug.Log("SOCKETJACK_PIPELINE_RENDER " + JsonUtility.ToJson(receipt));
                EditorApplication.update -= Tick; RestorePipeline(); EditorApplication.Exit(receipt.result == "passed" ? 0 : 1);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.update -= Tick; RestorePipeline(); EditorApplication.Exit(1); }
        }
        private static void RestorePipeline()
        { GraphicsSettings.defaultRenderPipeline = originalGraphics; QualitySettings.renderPipeline = originalQuality; }
        private static string Argument(string key)
        { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); if (i < 0 || i + 1 >= args.Length) throw new ArgumentException(key); return args[i + 1]; }
    }
}

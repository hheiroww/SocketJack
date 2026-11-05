using System.IO;
using SocketJack.Samples.Multiplayer;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SocketJack.Samples.Multiplayer.Editor
{
    public static class MultiplayerSampleBuild
    {
        [MenuItem("Tools/SocketJack/Samples/Create complete multiplayer 3D scene")]
        public static void Create3D() => Create("Assets/SocketJackSamples/Complete3D.unity", false);
        [MenuItem("Tools/SocketJack/Samples/Create complete multiplayer 2D scene")]
        public static void Create2D() => Create("Assets/SocketJackSamples/Complete2D.unity", true);
        public static void PrepareScenes(string root)
        { Create(root + "/Complete3D.unity", false); Create(root + "/Complete2D.unity", true); }
        private static void Create(string path, bool twoD)
        {
            if (File.Exists(path)) { Debug.Log("Existing sample scene retained: " + path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var demo = new GameObject("SocketJack complete multiplayer demo").AddComponent<MultiplayerGame>(); demo.Use2D = twoD;
            string materialPath = Path.GetDirectoryName(path).Replace('\\', '/') + "/Resources/SocketJackCompleteStandard.mat";
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)); AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null) { var shader = Shader.Find("Standard"); if (shader == null) throw new System.InvalidOperationException("Assign a compatible sample material for this renderer."); material = new Material(shader); AssetDatabase.CreateAsset(material, materialPath); }
            demo.MaterialTemplate = material;
            if (!EditorSceneManager.SaveScene(scene, path)) throw new IOException("Could not save the sample scene.");
            AssetDatabase.SaveAssets();
        }
    }
}

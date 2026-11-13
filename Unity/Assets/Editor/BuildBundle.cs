using System;
using System.IO;
using System.Linq;
using UnityEditor;

// Builds GameData/BDArmory/AssetBundles/bdarmorygpu_windows.bundle from every shader and compute shader in Assets/Shaders.
// Batch: Unity.exe -batchmode -nographics -quit -projectPath Unity -executeMethod BuildBundle.Build -outputPath <dir> -logFile <log>
public static class BuildBundle
{
    const string BundleName = "bdarmorygpu_windows.bundle";

    [MenuItem("Assets/Build BDArmory GPU Bundle Win64")]
    public static void Build()
    {
        const string staging = "Build";
        Directory.CreateDirectory(staging);
        string[] assets = Directory.GetFiles("Assets/Shaders")
            .Where(f => f.EndsWith(".shader") || f.EndsWith(".compute"))
            .Select(f => f.Replace('\\', '/'))
            .ToArray();
        var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = assets };
        var manifest = BuildPipeline.BuildAssetBundles(staging, new[] { build }, BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64);
        if (manifest == null)
            throw new Exception("BuildBundle: build failed");
        string output = ArgValue("-outputPath");
        if (output != null)
        {
            Directory.CreateDirectory(output);
            File.Copy(Path.Combine(staging, BundleName), Path.Combine(output, BundleName), true);
        }
    }

    static string ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; ++i)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}

using UnityEditor;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>A scratch folder under Assets for clips the tests create; emptied and removed after each test.</summary>
    static class TempAssets
    {
        public const string Folder = "Assets/__HumanoidClipEditorTests";

        public static string Create()
        {
            Delete();
            AssetDatabase.CreateFolder("Assets", "__HumanoidClipEditorTests");
            return Folder;
        }

        public static void Delete()
        {
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        public static string PathFor(string fileName) => $"{Folder}/{fileName}";
    }
}

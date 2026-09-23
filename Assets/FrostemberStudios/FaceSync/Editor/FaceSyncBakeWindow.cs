using UnityEngine;
using UnityEditor;

namespace Frostember.FaceSync.EditorTools
{
    public class FaceSyncBakeWindow : EditorWindow
    {
        private AudioClip sourceAudio;
        private VisemeMappingProfile mappingProfile;
        private FaceSyncController targetController;
 
        [MenuItem("Tools/Frostember Studios/FaceSync/FaceSync Baker")]
        public static void ShowWindow()
        {
            var window = GetWindow<FaceSyncBakeWindow>("FaceSync Baker");
            window.minSize = new Vector2(400, 350);
        }
 
        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Frostember FaceSync - Offline Baker", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Convert audio files into optimized FaceSync Data Assets. \n" +
                "These assets are highly optimized for mobile and VR, providing perfect stability for Humanoid characters.", MessageType.Info);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Baking Source", EditorStyles.boldLabel);
            sourceAudio = (AudioClip)EditorGUILayout.ObjectField("Input Audio Clip", sourceAudio, typeof(AudioClip), false);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Character & Mapping", EditorStyles.boldLabel);
            
            EditorGUI.BeginChangeCheck();
            targetController = (FaceSyncController)EditorGUILayout.ObjectField("Target Character", targetController, typeof(FaceSyncController), true);
            if (EditorGUI.EndChangeCheck() && targetController != null)
            {
                mappingProfile = targetController.lipSync.profile;
            }
 
            mappingProfile = (VisemeMappingProfile)EditorGUILayout.ObjectField("FaceSync Profile", mappingProfile, typeof(VisemeMappingProfile), false);
            
            EditorGUILayout.Space(10);
 
            if (targetController == null)
            {
                EditorGUILayout.HelpBox("Select a Character with a FaceSyncController to begin.", MessageType.Warning);
            }
            else if (sourceAudio == null)
            {
                EditorGUILayout.HelpBox("Assign an Audio Clip to bake.", MessageType.Info);
            }
            else if (mappingProfile == null)
            {
                EditorGUILayout.HelpBox("Assign a FaceSync Mapping Profile.", MessageType.Warning);
            }
 
            GUILayout.FlexibleSpace();
 
            GUI.enabled = sourceAudio != null && mappingProfile != null && targetController != null;
            
            Color oldColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.1f, 0.8f, 0.2f, 1f); 
            
            if (GUILayout.Button("BAKE TO FACESYNC ASSET", GUILayout.Height(50)))
            {
                Bake();
            }
            
            GUI.backgroundColor = oldColor;
            GUI.enabled = true;
            
            EditorGUILayout.Space(5);
        }
 
        private void Bake()
        {
            FaceSyncAnimationAsset asset = FaceSyncBaker.BakeAudioToDataAsset(sourceAudio, targetController);
            if (asset != null)
            {
                FaceSyncBaker.SaveFaceSyncDataAsset(asset, sourceAudio.name);
                Debug.Log($"<color=green>[FaceSync Baker]</color> Successfully baked <b>{sourceAudio.name}</b> to Data Asset!");
                EditorUtility.DisplayDialog("Baking Complete", $"Animation data for '{sourceAudio.name}' has been saved to the project.", "Awesome!");
            }
            else
            {
                EditorUtility.DisplayDialog("Error", "Baking failed. Please check the console for details.", "OK");
            }
        }
    }
}

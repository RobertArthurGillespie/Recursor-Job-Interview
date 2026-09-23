using UnityEngine;
using UnityEditor;
using Frostember.FaceSync;

namespace Frostember.FaceSync.EditorTools
{
    [CustomEditor(typeof(FaceSyncPlayer))]
    public class FaceSyncPlayerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
 
            FaceSyncPlayer player = (FaceSyncPlayer)target;
 
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Controlled Components", EditorStyles.boldLabel);
            

            var master = player.GetComponentInChildren<FaceSyncController>();
            if (master == null) master = player.GetComponentInParent<FaceSyncController>();

            DrawComponentStatus("FaceSync Master Controller", master != null);

            if (master != null)
            {
                EditorGUI.indentLevel++;
                DrawModuleStatus("Mouth (LipSync)", master.lipSync.enabled);
                DrawModuleStatus("Eyes (Blinking)", master.eyes.enabled);
                DrawModuleStatus("Head (Motion)", master.head.enabled);
                EditorGUI.indentLevel--;

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Performance & LOD", EditorStyles.boldLabel);
                
                if (Camera.main != null)
                {
                    float dist = Vector3.Distance(player.transform.position, Camera.main.transform.position);
                    EditorGUILayout.LabelField("Current Distance", $"{dist:F1} m");
                    
                    string lodStatus = "HIGH (Full)";
                    if (dist > master.lipSync.lodOffDistance) lodStatus = "OFF (Hidden)";
                    else if (dist > master.lipSync.lodMediumDistance) lodStatus = "MEDIUM (Reduced FPS)";
                    else if (master.lipSync.frustumCulling && master.lipSync.targetMesh != null && !master.lipSync.targetMesh.isVisible) lodStatus = "CULL (Frustum)";

                    EditorGUILayout.LabelField("Current LOD Level", lodStatus);
                }
                else
                {
                    EditorGUILayout.HelpBox("Camera.main not found. LOD system uses Main Camera for distance checks.", MessageType.Warning);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("⚠️ FaceSync Master Controller NOT found! \n" +
                    "FaceSyncPlayer requires the FaceSyncController script to be on the same object or its children for movement.", MessageType.Error);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Playback Controls", EditorStyles.boldLabel);

            GUI.enabled = Application.isPlaying;
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("TEST PLAY", GUILayout.Height(30)))
            {
                player.Play();
            }

            if (GUILayout.Button("STOP", GUILayout.Height(30)))
            {
                player.Stop();
            }
            EditorGUILayout.EndHorizontal();

            GUI.enabled = true;

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Play/Stop buttons only function in Play Mode.", MessageType.Info);
            }
        }

        private void DrawComponentStatus(string label, bool active)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(180));
            if (active)
            {
                GUI.color = Color.green;
                EditorGUILayout.LabelField("✅ Connected");
            }
            else
            {
                GUI.color = Color.red;
                EditorGUILayout.LabelField("❌ Missing component");
            }
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawModuleStatus(string label, bool active)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(180));
            if (active)
            {
                GUI.color = new Color(0.7f, 1f, 0.7f);
                EditorGUILayout.LabelField("✔ Active Module");
            }
            else
            {
                GUI.color = Color.gray;
                EditorGUILayout.LabelField("✖ Disabled");
            }
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();
        }
    }
}

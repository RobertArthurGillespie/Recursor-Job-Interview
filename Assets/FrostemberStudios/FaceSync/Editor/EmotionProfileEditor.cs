using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Frostember.FaceSync;

namespace Frostember.FaceSync.Editor
{
    [CustomEditor(typeof(EmotionProfile))]
    public class EmotionProfileEditor : UnityEditor.Editor
    {
        private EmotionProfile profile;
        private string[] availableBlendShapes = new string[0];
        
        // Temporary reference for previewing in the scene (cannot be saved in ScriptableObject)
        private GameObject previewTarget;

        private void OnEnable()
        {
            profile = (EmotionProfile)target;
            UpdateBlendShapeList();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Frostember FaceSync Emotion Mapping", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("This profile defines emotional states by combining multiple blend shapes and their weights.", MessageType.Info);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("editorReferencePrefab"));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                UpdateBlendShapeList();
            }

            if (profile.editorReferencePrefab == null)
            {
                EditorGUILayout.HelpBox("Drag and drop the character's .FBX model (or Prefab) from the Project window to automatically list the Blend Shapes in a dropdown.", MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene Preview", EditorStyles.boldLabel);
            previewTarget = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Preview Model (Scene)", "Drag a character from the Scene here to preview emotions."), previewTarget, typeof(GameObject), true);
            
            if (previewTarget == null)
            {
                EditorGUILayout.HelpBox("To use the 'Preview' buttons, drag your character from the Scene Hierarchy into the field above.", MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("globalWeightMultiplier"));
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Emotions Mappings", EditorStyles.boldLabel);

            SerializedProperty emotionsList = serializedObject.FindProperty("emotions");

            if (emotionsList.arraySize == 0)
            {
                EditorGUILayout.HelpBox("The profile is currently empty. Add a new emotion to start.", MessageType.Info);
            }

            for (int i = 0; i < emotionsList.arraySize; i++)
            {
                SerializedProperty emotion = emotionsList.GetArrayElementAtIndex(i);
                SerializedProperty emotionName = emotion.FindPropertyRelative("emotionName");
                SerializedProperty blendShapes = emotion.FindPropertyRelative("blendShapes");

                EditorGUILayout.BeginVertical("box");
                
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Emotion:", GUILayout.Width(60));
                emotionName.stringValue = EditorGUILayout.TextField(emotionName.stringValue, GUILayout.Width(130));
                
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("👁 Preview", GUILayout.Width(80)))
                {
                    PreviewEmotion(profile.emotions[i]);
                }

                if (GUILayout.Button("+ Add Blend Shape", GUILayout.Width(130)))
                {
                    blendShapes.arraySize++;
                }
                GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
                if (GUILayout.Button("X", GUILayout.Width(30)))
                {
                    emotionsList.DeleteArrayElementAtIndex(i);
                    GUI.backgroundColor = Color.white;
                    break;
                }
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();

                EditorGUI.indentLevel++;
                for (int j = 0; j < blendShapes.arraySize; j++)
                {
                    SerializedProperty bsWeight = blendShapes.GetArrayElementAtIndex(j);
                    SerializedProperty bsName = bsWeight.FindPropertyRelative("blendShapeName");
                    SerializedProperty weight = bsWeight.FindPropertyRelative("weight");

                    EditorGUILayout.BeginHorizontal();

                    if (availableBlendShapes.Length > 0)
                    {
                        int currentIndex = System.Array.IndexOf(availableBlendShapes, bsName.stringValue);
                        if (currentIndex == -1 && !string.IsNullOrEmpty(bsName.stringValue)) currentIndex = 0; 
                        
                        int selectedIndex = EditorGUILayout.Popup(Mathf.Max(0, currentIndex), availableBlendShapes);
                        if (selectedIndex >= 0 && selectedIndex < availableBlendShapes.Length)
                        {
                            bsName.stringValue = availableBlendShapes[selectedIndex];
                        }
                    }
                    else
                    {
                        bsName.stringValue = EditorGUILayout.TextField(bsName.stringValue);
                    }

                    EditorGUILayout.PropertyField(weight, GUIContent.none, GUILayout.Width(120));
                    
                    if (GUILayout.Button("-", GUILayout.Width(25)))
                    {
                        blendShapes.DeleteArrayElementAtIndex(j);
                        break;
                    }
                    
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUI.indentLevel--;
                
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(0.8f, 1f, 0.8f);
            if (GUILayout.Button("Add New Emotion", GUILayout.Height(30)))
            {
                emotionsList.arraySize++;
                SerializedProperty newEmotion = emotionsList.GetArrayElementAtIndex(emotionsList.arraySize - 1);
                newEmotion.FindPropertyRelative("emotionName").stringValue = "New Emotion";
            }
            
            GUI.backgroundColor = new Color(1f, 0.9f, 0.6f);
            if (GUILayout.Button("Reset Face (Clear Preview)", GUILayout.Height(30), GUILayout.Width(180)))
            {
                ResetFacePreview();
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            serializedObject.ApplyModifiedProperties();
        }

        private void PreviewEmotion(EmotionMapping mapping)
        {
            if (previewTarget == null)
            {
                Debug.LogWarning("[FaceSync Editor] Assign a Preview Model from the Scene to preview emotions.");
                return;
            }

            SkinnedMeshRenderer[] renderers = previewTarget.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) return;

            // Reset all first
            ResetFacePreview();

            float mult = profile.globalWeightMultiplier;

            foreach (var r in renderers)
            {
                if (r.sharedMesh == null) continue;
                
                foreach (var bs in mapping.blendShapes)
                {
                    int idx = r.sharedMesh.GetBlendShapeIndex(bs.blendShapeName);
                    if (idx >= 0)
                    {
                        r.SetBlendShapeWeight(idx, bs.weight * mult);
                    }
                }
            }
        }

        private void ResetFacePreview()
        {
            if (previewTarget == null) return;
            
            SkinnedMeshRenderer[] renderers = previewTarget.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var r in renderers)
            {
                if (r.sharedMesh == null) continue;
                for (int i = 0; i < r.sharedMesh.blendShapeCount; i++)
                {
                    r.SetBlendShapeWeight(i, 0f);
                }
            }
        }

        private void UpdateBlendShapeList()
        {
            if (profile != null && profile.editorReferencePrefab != null)
            {
                SkinnedMeshRenderer[] renderers = profile.editorReferencePrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                List<string> foundShapes = new List<string>();
                
                foreach(var r in renderers)
                {
                    if (r.sharedMesh != null)
                    {
                        for (int i = 0; i < r.sharedMesh.blendShapeCount; i++)
                        {
                            string shapeName = r.sharedMesh.GetBlendShapeName(i);
                            if (!foundShapes.Contains(shapeName)) foundShapes.Add(shapeName);
                        }
                    }
                }
                availableBlendShapes = foundShapes.ToArray();
            }
            else
            {
                availableBlendShapes = new string[0];
            }
        }
    }
}

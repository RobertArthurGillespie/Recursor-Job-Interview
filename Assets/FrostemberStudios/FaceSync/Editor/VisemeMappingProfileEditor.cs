using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Frostember.FaceSync;

namespace Frostember.FaceSync.Editor
{
    [CustomEditor(typeof(VisemeMappingProfile))]
    public class VisemeMappingProfileEditor : UnityEditor.Editor
    {
        private VisemeMappingProfile profile;
        private string[] availableBlendShapes = new string[0];

        private void OnEnable()
        {
            profile = (VisemeMappingProfile)target;
            UpdateBlendShapeList();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space();
            Texture2dHeader();
            EditorGUILayout.LabelField("Frostember Universal FaceSync Mapping", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("This profile defines the translation from Meta Oculus Audio (15 visemes) to your model's blend shapes (SkinnedMeshRenderer).", MessageType.Info);
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
                EditorGUILayout.HelpBox("Drag and drop the character's .FBX model (or Prefab) from the Project window (not from the scene!) to automatically list the Blend Shapes.", MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("globalWeightMultiplier"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothingSpeed"));
            EditorGUILayout.Space();

            if (GUILayout.Button("Reset & Auto-fill Default 15 Visemes"))
            {
                Debug.Log("[LipSync Editor] Auto-Fill button pressed.");
                Undo.RecordObject(profile, "Initialize Visemes");
                profile.InitializeDefaultVisemes();
                EditorUtility.SetDirty(profile);
                
                bool updated = serializedObject.UpdateIfRequiredOrScript(); 
                Debug.Log($"[LipSync Editor] SerializedObject.Update() completed with change result: {updated}");
                Repaint(); 
            }

            EditorGUILayout.Space(2);
            GUI.backgroundColor = new Color(0.8f, 1f, 0.8f);
            if (GUILayout.Button("Auto-fill CC3+ / CC5 (Ultra-Realism Profile)", GUILayout.Height(30)))
            {
                Debug.Log("[LipSync Editor] CC3+ Auto-Fill button pressed.");
                Undo.RecordObject(profile, "Initialize CC3+ Visemes");
                profile.InitializeCC3PlusVisemes();
                EditorUtility.SetDirty(profile);
                
                serializedObject.UpdateIfRequiredOrScript(); 
                Repaint(); 
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(2);
            GUI.backgroundColor = new Color(0.8f, 0.9f, 1f);
            if (GUILayout.Button("Auto-fill ARKit (Standard Face - MetaHumans / RPM)", GUILayout.Height(30)))
            {
                Debug.Log("[LipSync Editor] ARKit Auto-Fill button pressed.");
                Undo.RecordObject(profile, "Initialize ARKit Visemes");
                profile.InitializeARKitVisemes();
                EditorUtility.SetDirty(profile);
                
                serializedObject.UpdateIfRequiredOrScript(); 
                Repaint(); 
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Viseme Mappings", EditorStyles.boldLabel);

            SerializedProperty mappingsList = serializedObject.FindProperty("mappings");

            if (mappingsList.arraySize == 0)
            {
                EditorGUILayout.HelpBox("The profile is currently empty. Click the Auto-fill button above to generate the basic visemes for mapping.", MessageType.Info);
            }

            for (int i = 0; i < mappingsList.arraySize; i++)
            {
                SerializedProperty mapping = mappingsList.GetArrayElementAtIndex(i);
                SerializedProperty viseme = mapping.FindPropertyRelative("viseme");
                SerializedProperty blendShapes = mapping.FindPropertyRelative("blendShapes");

                EditorGUILayout.BeginVertical("box");
                
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(viseme, GUIContent.none, GUILayout.Width(130));
                
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("+ Add Blend Shape", GUILayout.Width(140)))
                {
                    blendShapes.arraySize++;
                }
                if (GUILayout.Button("X", GUILayout.Width(30)))
                {
                    mappingsList.DeleteArrayElementAtIndex(i);
                    break;
                }
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

            if (GUILayout.Button("Add Extra Custom Mapping"))
            {
                mappingsList.arraySize++;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void Texture2dHeader()
        {

        }
        private void UpdateBlendShapeList()
        {
            if (profile != null && profile.editorReferencePrefab != null)
            {
                Debug.Log($"[FaceSync Editor] Searching for SkinnedMeshRenderers on the assigned object: {profile.editorReferencePrefab.name}");
                

                SkinnedMeshRenderer[] renderers = profile.editorReferencePrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                
                if (renderers.Length == 0)
                {
                    Debug.LogWarning($"[FaceSync Editor] NO SkinnedMeshRenderer was found on object '{profile.editorReferencePrefab.name}' or its children! Are you sure this is a CC5 model or has a facial mesh?");
                }
                else
                {
                    Debug.Log($"[FaceSync Editor] Found {renderers.Length} SMR components on this object.");
                }
 
                List<string> foundShapes = new List<string>();
 
                foreach(var r in renderers)
                {
                    if (r.sharedMesh != null)
                    {
                        Debug.Log($"[FaceSync Editor] Analyzing SMR '{r.name}' with mesh '{r.sharedMesh.name}'. Blend Shapes count: {r.sharedMesh.blendShapeCount}");
                        for (int i = 0; i < r.sharedMesh.blendShapeCount; i++)
                        {
                            string shapeName = r.sharedMesh.GetBlendShapeName(i);
                            if (!foundShapes.Contains(shapeName))
                            {
                                foundShapes.Add(shapeName);
                            }
                        }
                    }
                    else
                    {
                        Debug.LogError($"[FaceSync Editor] SMR '{r.name}' HAS NO sharedMesh assigned! Check the FBX import.");
                    }
                }
 
                availableBlendShapes = foundShapes.ToArray();
                Debug.Log($"[FaceSync Editor] Total unique Blend Shapes collected for dropdown: {availableBlendShapes.Length}");
            }
            else
            {
                if (profile != null) Debug.Log("[FaceSync Editor] EditorReferencePrefab is empty, clearing old dropdown list.");
                availableBlendShapes = new string[0];
            }
        }
    }
}

using UnityEngine;
using UnityEditor;
using Frostember.FaceSync.Timeline;

namespace Frostember.FaceSync.Editor
{
    [CustomEditor(typeof(FaceSyncEmotionClip))]
    public class FaceSyncEmotionClipEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            FaceSyncEmotionClip clip = (FaceSyncEmotionClip)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("FaceSync Emotion Settings", EditorStyles.boldLabel);

            SerializedProperty profileProp = serializedObject.FindProperty("profile");
            EditorGUILayout.PropertyField(profileProp, new GUIContent("Emotion Profile"));

            if (profileProp.objectReferenceValue != null)
            {
                EmotionProfile profile = (EmotionProfile)profileProp.objectReferenceValue;

                if (profile.emotions != null && profile.emotions.Count > 0)
                {
                    SerializedProperty templateProp = serializedObject.FindProperty("template");
                    SerializedProperty emotionNameProp = templateProp.FindPropertyRelative("emotionName");
                    
                    string[] options = new string[profile.emotions.Count];
                    int currentIndex = 0;
                    
                    for (int i = 0; i < profile.emotions.Count; i++)
                    {
                        options[i] = profile.emotions[i].emotionName;
                        if (options[i] == emotionNameProp.stringValue)
                        {
                            currentIndex = i;
                        }
                    }

                    EditorGUILayout.Space();
                    int newIndex = EditorGUILayout.Popup("Select Emotion", currentIndex, options);
                    
                    if (newIndex >= 0 && newIndex < options.Length)
                    {
                        emotionNameProp.stringValue = options[newIndex];
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("The selected Emotion Profile has no emotions defined.", MessageType.Warning);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Assign an Emotion Profile to select emotions from a dropdown.", MessageType.Info);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}

using UnityEngine;
using UnityEditor;
using Frostember.FaceSync;
//using Oculus.LipSync;

namespace Frostember.FaceSync.EditorTools
{
    [CustomEditor(typeof(FaceSyncController))]
    public class FaceSyncControllerEditor : UnityEditor.Editor
    {
        private SerializedProperty lipSyncProp;
        private SerializedProperty emotionsProp;
        private SerializedProperty eyesProp;
        private SerializedProperty headProp;
        private SerializedProperty ikLookProp;

        private void OnEnable()
        {
            lipSyncProp = serializedObject.FindProperty("lipSync");
            emotionsProp = serializedObject.FindProperty("emotions");
            eyesProp = serializedObject.FindProperty("eyes");
            headProp = serializedObject.FindProperty("head");
            ikLookProp = serializedObject.FindProperty("ikLook");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("FaceSync Master Controller", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("This unified component manages all facial features. Toggle modules below to configure.", MessageType.Info);
            EditorGUILayout.Space();


            DrawLipSyncSection();

            DrawEmotionsSection();

            DrawEyeSection();


            DrawModuleSection("💆 Head & Neck Motion", headProp, new Color(0.8f, 0.6f, 0.2f, 0.15f));

            DrawModuleSection("🎯 IK Look Tracking", ikLookProp, new Color(0.9f, 0.4f, 0.1f, 0.15f));

            EditorGUILayout.Space();
            if (GUILayout.Button("Open Setup Wizard", GUILayout.Height(30)))
            {
                FaceSyncSetupWizard.ShowWindow();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawEyeSection()
        {
            FaceSyncController master = (FaceSyncController)target;
            SerializedProperty enabledProp = eyesProp.FindPropertyRelative("enabled");

            Rect rect = EditorGUILayout.BeginVertical();
            EditorGUI.DrawRect(new Rect(rect.x - 2, rect.y - 2, rect.width + 4, rect.height + 4), new Color(0.2f, 0.4f, 0.8f, 0.15f));
            
            EditorGUILayout.BeginHorizontal();
            enabledProp.boolValue = EditorGUILayout.ToggleLeft("👁️ Eyes & Blinking", enabledProp.boolValue, EditorStyles.boldLabel, GUILayout.Width(200));
            GUI.color = enabledProp.boolValue ? Color.green : Color.gray;
            EditorGUILayout.LabelField(enabledProp.boolValue ? "● ACTIVE" : "○ DISABLED", EditorStyles.miniLabel);
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            if (enabledProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.Space(2);
                
                SerializedProperty targetMeshProp = eyesProp.FindPropertyRelative("targetMesh");
                EditorGUILayout.PropertyField(targetMeshProp);
                
                SkinnedMeshRenderer effectiveMesh = (targetMeshProp.objectReferenceValue as SkinnedMeshRenderer) ?? master.lipSync.targetMesh;

                DrawBlendShapeField("Left Blink", eyesProp.FindPropertyRelative("leftEyeName"), effectiveMesh);
                DrawBlendShapeField("Right Blink", eyesProp.FindPropertyRelative("rightEyeName"), effectiveMesh);
                
                EditorGUILayout.PropertyField(eyesProp.FindPropertyRelative("minBlinkInterval"));
                EditorGUILayout.PropertyField(eyesProp.FindPropertyRelative("maxBlinkInterval"));
                EditorGUILayout.PropertyField(eyesProp.FindPropertyRelative("blinkSpeed"));
                EditorGUILayout.PropertyField(eyesProp.FindPropertyRelative("maxBlinkWeight"));

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Look Blendshapes", EditorStyles.miniBoldLabel);
                
                DrawBlendShapeField("Look Up Left", eyesProp.FindPropertyRelative("eyeLookUpL"), effectiveMesh);
                DrawBlendShapeField("Look Up Right", eyesProp.FindPropertyRelative("eyeLookUpR"), effectiveMesh);
                DrawBlendShapeField("Look Down Left", eyesProp.FindPropertyRelative("eyeLookDownL"), effectiveMesh);
                DrawBlendShapeField("Look Down Right", eyesProp.FindPropertyRelative("eyeLookDownR"), effectiveMesh);
                DrawBlendShapeField("Look Left Left", eyesProp.FindPropertyRelative("eyeLookLeftL"), effectiveMesh);
                DrawBlendShapeField("Look Left Right", eyesProp.FindPropertyRelative("eyeLookLeftR"), effectiveMesh);
                DrawBlendShapeField("Look Right Left", eyesProp.FindPropertyRelative("eyeLookRightL"), effectiveMesh);
                DrawBlendShapeField("Look Right Right", eyesProp.FindPropertyRelative("eyeLookRightR"), effectiveMesh);

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Movement", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(eyesProp.FindPropertyRelative("enableSaccadic"));
                EditorGUILayout.PropertyField(eyesProp.FindPropertyRelative("saccadicInterval"));
                EditorGUILayout.PropertyField(eyesProp.FindPropertyRelative("saccadicStrength"));
                EditorGUILayout.PropertyField(eyesProp.FindPropertyRelative("eyeMovementSpeed"));
                EditorGUILayout.PropertyField(eyesProp.FindPropertyRelative("enableMicroJitter"));
                EditorGUILayout.PropertyField(eyesProp.FindPropertyRelative("microJitterStrength"));

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(5);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(10);
        }

        private void DrawBlendShapeField(string label, SerializedProperty property, SkinnedMeshRenderer mesh)
        {
            if (mesh == null || mesh.sharedMesh == null)
            {
                EditorGUILayout.PropertyField(property, new GUIContent(label));
                return;
            }

            int count = mesh.sharedMesh.blendShapeCount;
            string[] names = new string[count + 1];
            names[0] = "--- None ---";
            int currentIndex = 0;

            for (int i = 0; i < count; i++)
            {
                string bsName = mesh.sharedMesh.GetBlendShapeName(i);
                names[i + 1] = bsName;
                if (bsName == property.stringValue) currentIndex = i + 1;
            }

            int nextIndex = EditorGUILayout.Popup(label, currentIndex, names);
            if (nextIndex == 0) property.stringValue = "";
            else property.stringValue = names[nextIndex];
        }

        private void DrawLipSyncSection()
        {
            FaceSyncController master = (FaceSyncController)target;
            SerializedProperty enabledProp = lipSyncProp.FindPropertyRelative("enabled");


            Rect rect = EditorGUILayout.BeginVertical();
            EditorGUI.DrawRect(new Rect(rect.x - 2, rect.y - 2, rect.width + 4, rect.height + 4), new Color(0.2f, 0.7f, 0.2f, 0.15f));
            
            EditorGUILayout.BeginHorizontal();
            enabledProp.boolValue = EditorGUILayout.ToggleLeft("🫦 LipSync (Speech)", enabledProp.boolValue, EditorStyles.boldLabel, GUILayout.Width(200));
            GUI.color = enabledProp.boolValue ? Color.green : Color.gray;
            EditorGUILayout.LabelField(enabledProp.boolValue ? "● ACTIVE" : "○ DISABLED", EditorStyles.miniLabel);
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            if (enabledProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.Space(2);
                

                SerializedProperty endProp = lipSyncProp.GetEndProperty();
                SerializedProperty it = lipSyncProp.Copy();
                it.NextVisible(true);
                while (it.NextVisible(false) && !SerializedProperty.EqualContents(it, endProp))
                {
                    EditorGUILayout.PropertyField(it, true);
                }

                SerializedProperty modeProp = lipSyncProp.FindPropertyRelative("mode");
                if (modeProp.enumValueIndex == (int)FaceSyncController.LipSyncMode.LiveMicrophone)
                {
                    HandleMicrophoneSetup(master);
                }


                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Oculus LipSync Source", EditorStyles.boldLabel);
                
                if (master.contextReference == null) master.contextReference = master.GetComponent<OVRLipSyncContext>();

                if (master.contextReference != null)
                {
                    SerializedObject contextSO = new SerializedObject(master.contextReference);
                    contextSO.Update();
                    
                    SerializedProperty providerProp = contextSO.FindProperty("provider");
                    if (providerProp != null) EditorGUILayout.PropertyField(providerProp, new GUIContent("LipSync Provider"));

                    SerializedProperty audioSourceProp = contextSO.FindProperty("audioSource");
                    if (audioSourceProp != null) EditorGUILayout.PropertyField(audioSourceProp, new GUIContent("Audio Source"));


                    EditorGUILayout.HelpBox("If using Microphone, ensure 'Mic' is selected in the provider or a Microphone Input script is present.", MessageType.None);

                    contextSO.ApplyModifiedProperties();
                }
                else
                {
                    EditorGUILayout.HelpBox("OVRLipSyncContext missing! Use Setup Wizard to fix.", MessageType.Error);
                }
                
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(5);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(10);
        }

        private void DrawEmotionsSection()
        {
            FaceSyncController master = (FaceSyncController)target;
            SerializedProperty enabledProp = emotionsProp.FindPropertyRelative("enabled");

            Rect rect = EditorGUILayout.BeginVertical();
            EditorGUI.DrawRect(new Rect(rect.x - 2, rect.y - 2, rect.width + 4, rect.height + 4), new Color(0.8f, 0.3f, 0.4f, 0.15f));
            
            EditorGUILayout.BeginHorizontal();
            enabledProp.boolValue = EditorGUILayout.ToggleLeft("🎭 Emotions & Expressions", enabledProp.boolValue, EditorStyles.boldLabel, GUILayout.Width(200));
            GUI.color = enabledProp.boolValue ? Color.green : Color.gray;
            EditorGUILayout.LabelField(enabledProp.boolValue ? "● ACTIVE" : "○ DISABLED", EditorStyles.miniLabel);
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            if (enabledProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.Space(2);

                SerializedProperty profileProp = emotionsProp.FindPropertyRelative("profile");
                EditorGUILayout.PropertyField(profileProp);
                EditorGUILayout.PropertyField(emotionsProp.FindPropertyRelative("transitionSpeed"));
                
                EditorGUILayout.Space(5);

                if (profileProp.objectReferenceValue != null)
                {
                    EmotionProfile emotionProfile = (EmotionProfile)profileProp.objectReferenceValue;
                    
                    if (emotionProfile.emotions != null && emotionProfile.emotions.Count > 0)
                    {
                        EditorGUILayout.LabelField("Play Mode Testing", EditorStyles.miniBoldLabel);

                        SerializedProperty currentEmotionProp = emotionsProp.FindPropertyRelative("currentEmotion");

                        string[] emotionNames = new string[emotionProfile.emotions.Count];
                        int currentIndex = 0;
                        string currentSelected = currentEmotionProp.stringValue;

                        for (int i = 0; i < emotionProfile.emotions.Count; i++)
                        {
                            emotionNames[i] = emotionProfile.emotions[i].emotionName;
                            if (emotionNames[i] == currentSelected) currentIndex = i;
                        }

                        EditorGUILayout.BeginHorizontal();
                        int newIndex = EditorGUILayout.Popup(currentIndex, emotionNames, GUILayout.Width(200));
                        string selectedEmotionName = emotionNames[newIndex];
                        
                        GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
                        if (GUILayout.Button("▶ Play", GUILayout.Width(60)))
                        {
                            currentEmotionProp.stringValue = selectedEmotionName;
                        }
                        
                        GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
                        if (GUILayout.Button("⏹ Stop", GUILayout.Width(60)))
                        {
                            currentEmotionProp.stringValue = "";
                        }
                        GUI.backgroundColor = Color.white;
                        
                        EditorGUILayout.EndHorizontal();

                        if (!Application.isPlaying)
                        {
                            EditorGUILayout.HelpBox("Press Play in Unity to test emotions on the character in real-time.", MessageType.Info);
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("The assigned profile has no emotions defined.", MessageType.Warning);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Assign an Emotion Profile to extract and test emotions.", MessageType.Warning);
                }

                EditorGUI.indentLevel--;
                EditorGUILayout.Space(5);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(10);
        }

        private void HandleMicrophoneSetup(FaceSyncController master)
        {
            var micInput = master.GetComponent<Frostember_MicrophoneInput>();
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("🎤 Microphone Setup", EditorStyles.boldLabel);
            
            if (micInput == null)
            {
                EditorGUILayout.HelpBox("Microphone Input component is missing. Live LipSync requires it.", MessageType.Warning);
                if (GUILayout.Button("Add Microphone Input Component"))
                {
                    master.gameObject.AddComponent<Frostember_MicrophoneInput>();
                }
            }
            else
            {
                EditorGUILayout.LabelField("Status: Connected", EditorStyles.miniLabel);
                if (GUILayout.Button("Remove Microphone Support"))
                {
                    DestroyImmediate(micInput);
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private void DrawModuleSection(string title, SerializedProperty moduleProp, Color bgColor)
        {
            SerializedProperty enabledProp = moduleProp.FindPropertyRelative("enabled");


            Rect rect = EditorGUILayout.BeginVertical();
            EditorGUI.DrawRect(new Rect(rect.x - 2, rect.y - 2, rect.width + 4, rect.height + 4), bgColor);
            
            EditorGUILayout.BeginHorizontal();
            enabledProp.boolValue = EditorGUILayout.ToggleLeft(title, enabledProp.boolValue, EditorStyles.boldLabel, GUILayout.Width(200));
            
            if (enabledProp.boolValue)
            {
                GUI.color = Color.green;
                EditorGUILayout.LabelField("● ACTIVE", EditorStyles.miniLabel);
            }
            else
            {
                GUI.color = Color.gray;
                EditorGUILayout.LabelField("○ DISABLED", EditorStyles.miniLabel);
            }
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            if (enabledProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.Space(2);
                

                SerializedProperty endProp = moduleProp.GetEndProperty();
                SerializedProperty it = moduleProp.Copy();
                it.NextVisible(true);
                
                while (it.NextVisible(false) && !SerializedProperty.EqualContents(it, endProp))
                {
                    EditorGUILayout.PropertyField(it, true);
                }
                
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(5);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(10);
        }
    }
}

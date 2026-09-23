using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
//using Oculus.LipSync;

namespace Frostember.FaceSync.EditorTools
{
    public class FaceSyncSetupWizard : EditorWindow
    {
        private GameObject characterRoot;
        private bool addLipSync = true;
        private bool addEmotions = true;
        private bool addBlinking = true;
        private bool addHeadMotion = true;
        private bool addIK = false;
        private bool addPlayer = false;

        [MenuItem("Tools/Frostember Studios/FaceSync/Setup Wizard")]
        public static void ShowWindow()
        {
            GetWindow<FaceSyncSetupWizard>("FaceSync Setup");
        }

        private void OnGUI()
        {
            EditorGUIUtility.labelWidth = 250;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("FaceSync - Unified Character Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("This wizard will add a single 'FaceSync Controller' to your character and automatically configure all selected modules.", MessageType.Info);
            EditorGUILayout.Space();

            characterRoot = (GameObject)EditorGUILayout.ObjectField("Character Root (Player/NPC)", characterRoot, typeof(GameObject), true);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Configure Modules", EditorStyles.boldLabel);
            
            addLipSync = EditorGUILayout.Toggle("🫦 Enable LipSync (Speech)", addLipSync);
            addEmotions = EditorGUILayout.Toggle("🎭 Enable Emotions", addEmotions);
            addBlinking = EditorGUILayout.Toggle("👁️ Enable Auto-Blinking & Eyes", addBlinking);
            addHeadMotion = EditorGUILayout.Toggle("💆 Enable Auto-Head Motion", addHeadMotion);
            addIK = EditorGUILayout.Toggle("🎯 Enable IK Look Tracking", addIK);
            addPlayer = EditorGUILayout.Toggle("💾 Add FaceSync Player (for Baked data)", addPlayer);

            EditorGUILayout.Space(20);

            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("AUTO-SETUP MASTER COMPONENT", GUILayout.Height(40)))
            {
                PerformSetup();
            }
            GUI.backgroundColor = Color.white;
        }

        private void PerformSetup()
        {
            if (characterRoot == null)
            {
                EditorUtility.DisplayDialog("FaceSync Setup", "Please assign a Character Root object first.", "OK");
                return;
            }


            FaceSyncController master = characterRoot.GetComponent<FaceSyncController>();
            if (master == null) master = characterRoot.AddComponent<FaceSyncController>();


            master.lipSync.enabled = addLipSync;
            master.emotions.enabled = addEmotions;
            master.eyes.enabled = addBlinking;
            master.head.enabled = addHeadMotion;
            master.ikLook.enabled = addIK;



            

            if (addLipSync)
            {
                if (characterRoot.GetComponent<OVRLipSyncContextBase>() == null)
                {
                    characterRoot.AddComponent<OVRLipSyncContext>();
                }
            }


            if (addPlayer)
            {
                if (characterRoot.GetComponent<FaceSyncPlayer>() == null)
                {
                    characterRoot.AddComponent<FaceSyncPlayer>();
                }
            }

            EditorUtility.DisplayDialog("FaceSync Setup", "Character setup complete! Check the FaceSync Controller on the root object for further refinements.", "Great!");
            Selection.activeGameObject = characterRoot;
        }
    }
}

using UnityEngine;
using System.Collections.Generic;
//using Oculus.LipSync;

namespace Frostember.FaceSync
{
    [System.Serializable]
    public class BlendshapeWeight
    {
        [Tooltip("Name of the blend shape on the SkinnedMeshRenderer")]
        public string blendShapeName;
        
        [Tooltip("The weight multiplier (0 to 1 for normalized, although CC5 blendshapes in Unity are usually 0-100)")]
        [Range(0f, 1f)]
        public float weight = 1f;
    }

    [System.Serializable]
    public class VisemeMapping
    {
        [Tooltip("The Oculus Viseme this mapping responds to")]
        public OVRLipSync.Viseme viseme;
        
        [Tooltip("List of blend shapes affected by this viseme")]
        public List<BlendshapeWeight> blendShapes = new List<BlendshapeWeight>();
    }

    [CreateAssetMenu(fileName = "NewVisemeMapping", menuName = "Frostember Studios/FaceSync/Viseme Mapping Profile")]
    public class VisemeMappingProfile : ScriptableObject
    {
#if UNITY_EDITOR
        [Header("Editor Setup")]
        [Tooltip("Editor Only: Drag and drop the .FBX model or Prefab of your character from the Project window. We will automatically find the face mesh within it.")]
        public GameObject editorReferencePrefab;
#endif

        [Header("Global Settings")]
        [Tooltip("Multiplier for all blendshapes in this profile. CC5 and Unity handle blendshapes as 0-100 typically.")]
        public float globalWeightMultiplier = 175f; 

        [Tooltip("How fast the blendshapes transition. Higher = faster. Applied across the board.")]
        public float smoothingSpeed = 15f;

        [Header("Viseme Mappings")]
        public List<VisemeMapping> mappings = new List<VisemeMapping>();

        private void Reset()
        {
            InitializeDefaultVisemes();
        }




        public void InitializeDefaultVisemes()
        {
            Debug.Log("[FaceSync Profiles] Running InitializeDefaultVisemes()");
            if (mappings == null) 
            {
                Debug.LogWarning("[FaceSync Profiles] 'mappings' list was null, allocating a new List.");
                mappings = new List<VisemeMapping>();
            }
            
            if (mappings.Count == 0 || mappings.Count != 15)
            {
                Debug.Log($"[FaceSync Profiles] Filling 15 default visemes. Current count was: {mappings.Count}");
                mappings.Clear();
                foreach (OVRLipSync.Viseme v in System.Enum.GetValues(typeof(OVRLipSync.Viseme)))
                {
                    mappings.Add(new VisemeMapping { viseme = v });
                }
                Debug.Log($"[LipSync Data] Profile now contains {mappings.Count} visemes.");
            }
            else
            {
                Debug.Log("[LipSync Data] 'mappings' list already contains 15 entries, skipped filling.");
            }
        }




        public void InitializeCC3PlusVisemes()
        {
            Debug.Log("[LipSync Data] Running InitializeCC3PlusVisemes()");
            if (mappings == null) mappings = new List<VisemeMapping>();
            mappings.Clear();


            var m0 = new VisemeMapping { viseme = OVRLipSync.Viseme.sil };
            mappings.Add(m0);

            var m1 = new VisemeMapping { viseme = OVRLipSync.Viseme.PP };
            m1.blendShapes.Add(new BlendshapeWeight { blendShapeName = "B_M_P", weight = 1f });
            m1.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Lips_Together_UL", weight = 0.8f });
            m1.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Lips_Together_UR", weight = 0.8f });
            m1.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Lips_Together_DL", weight = 0.8f });
            m1.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Lips_Together_DR", weight = 0.8f });
            m1.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m1);

            var m2 = new VisemeMapping { viseme = OVRLipSync.Viseme.FF };
            m2.blendShapes.Add(new BlendshapeWeight { blendShapeName = "F_V", weight = 1f });
            m2.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_UpperLip_Raise_L", weight = 0.4f });
            m2.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_UpperLip_Raise_R", weight = 0.4f });
            m2.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m2);

            var m3 = new VisemeMapping { viseme = OVRLipSync.Viseme.TH };
            m3.blendShapes.Add(new BlendshapeWeight { blendShapeName = "TH", weight = 1f });
            m3.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Tongue_Out", weight = 0.6f });
            m3.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Tongue_Up", weight = 0.3f });
            m3.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m3);

            var m4 = new VisemeMapping { viseme = OVRLipSync.Viseme.DD };
            m4.blendShapes.Add(new BlendshapeWeight { blendShapeName = "T_L_D_N", weight = 1f });
            m4.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Tongue_Up", weight = 0.7f });
            m4.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m4);

            var m5 = new VisemeMapping { viseme = OVRLipSync.Viseme.kk };
            m5.blendShapes.Add(new BlendshapeWeight { blendShapeName = "K_G_H_NG", weight = 1f });
            m5.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m5);

            var m6 = new VisemeMapping { viseme = OVRLipSync.Viseme.CH };
            m6.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Ch_J", weight = 1f });
            m6.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Stretch_L", weight = 0.3f });
            m6.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Stretch_R", weight = 0.3f });
            m6.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Nose_Nasolabial_Deepen_L", weight = 0.3f });
            m6.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Nose_Nasolabial_Deepen_R", weight = 0.3f });
            m6.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m6);

            var m7 = new VisemeMapping { viseme = OVRLipSync.Viseme.SS };
            m7.blendShapes.Add(new BlendshapeWeight { blendShapeName = "S_Z", weight = 1f });
            m7.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Stretch_L", weight = 0.4f });
            m7.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Stretch_R", weight = 0.4f });
            m7.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Eye_Cheek_Raise_L", weight = 0.2f });
            m7.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Eye_Cheek_Raise_R", weight = 0.2f });
            m7.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m7);

            var m8 = new VisemeMapping { viseme = OVRLipSync.Viseme.nn };
            m8.blendShapes.Add(new BlendshapeWeight { blendShapeName = "T_L_D_N", weight = 0.8f });
            m8.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Tongue_Up", weight = 1f });
            m8.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m8);

            var m9 = new VisemeMapping { viseme = OVRLipSync.Viseme.RR };
            m9.blendShapes.Add(new BlendshapeWeight { blendShapeName = "R", weight = 1f });
            m9.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Tongue_Roll", weight = 0.5f });
            m9.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m9);

            var m10 = new VisemeMapping { viseme = OVRLipSync.Viseme.aa };
            m10.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Ah", weight = 1f });
            m10.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Tongue_Down", weight = 0.5f });
            m10.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m10);

            var m11 = new VisemeMapping { viseme = OVRLipSync.Viseme.E };
            m11.blendShapes.Add(new BlendshapeWeight { blendShapeName = "EE", weight = 1f });
            m11.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Stretch_L", weight = 0.6f });
            m11.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Stretch_R", weight = 0.6f });
            m11.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Eye_Cheek_Raise_L", weight = 0.4f });
            m11.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Eye_Cheek_Raise_R", weight = 0.4f });
            m11.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_LowerLip_Depress_L", weight = 0.2f });
            m11.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_LowerLip_Depress_R", weight = 0.2f });
            m11.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m11);

            var m12 = new VisemeMapping { viseme = OVRLipSync.Viseme.ih };
            m12.blendShapes.Add(new BlendshapeWeight { blendShapeName = "IH", weight = 1f });
            m12.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Stretch_L", weight = 0.4f });
            m12.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Mouth_Stretch_R", weight = 0.4f });
            m12.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Eye_Cheek_Raise_L", weight = 0.2f });
            m12.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Eye_Cheek_Raise_R", weight = 0.2f });
            m12.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m12);

            var m13 = new VisemeMapping { viseme = OVRLipSync.Viseme.oh };
            m13.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Oh", weight = 1f });
            m13.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m13);

            var m14 = new VisemeMapping { viseme = OVRLipSync.Viseme.ou };
            m14.blendShapes.Add(new BlendshapeWeight { blendShapeName = "W_OO", weight = 1f });
            m14.blendShapes.Add(new BlendshapeWeight { blendShapeName = "Teeth_Down_D", weight = 0.5f });
            mappings.Add(m14);

            Debug.Log($"[LipSync Data] CC3+ Profile successfully generated with {mappings.Count} visemes and auxiliary movements.");
        }





        public void InitializeARKitVisemes()
        {
            Debug.Log("[LipSync Data] Running InitializeARKitVisemes() with High-Fidelity Refinements");
            if (mappings == null) mappings = new List<VisemeMapping>();
            mappings.Clear();
 

            mappings.Add(new VisemeMapping { viseme = OVRLipSync.Viseme.sil });
 

            var mPP = new VisemeMapping { viseme = OVRLipSync.Viseme.PP };
            mPP.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthClose", weight = 1f });
            mPP.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthPucker", weight = 0.4f });
            mPP.blendShapes.Add(new BlendshapeWeight { blendShapeName = "cheekPuff", weight = 0.2f });
            mappings.Add(mPP);
 

            var mFF = new VisemeMapping { viseme = OVRLipSync.Viseme.FF };
            mFF.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthFunnel", weight = 0.8f });
            mFF.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthLowerDownLeft", weight = 0.5f });
            mFF.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthLowerDownRight", weight = 0.5f });
            mappings.Add(mFF);
 

            var mTH = new VisemeMapping { viseme = OVRLipSync.Viseme.TH };
            mTH.blendShapes.Add(new BlendshapeWeight { blendShapeName = "tongueOut", weight = 0.7f });
            mTH.blendShapes.Add(new BlendshapeWeight { blendShapeName = "jawOpen", weight = 0.15f });
            mappings.Add(mTH);
 

            var mDD = new VisemeMapping { viseme = OVRLipSync.Viseme.DD };
            mDD.blendShapes.Add(new BlendshapeWeight { blendShapeName = "jawOpen", weight = 0.25f });
            mDD.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthLowerDownLeft", weight = 0.3f });
            mDD.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthLowerDownRight", weight = 0.3f });
            mappings.Add(mDD);
 

            var mkk = new VisemeMapping { viseme = OVRLipSync.Viseme.kk };
            mkk.blendShapes.Add(new BlendshapeWeight { blendShapeName = "jawOpen", weight = 0.35f });
            mappings.Add(mkk);
 

            var mCH = new VisemeMapping { viseme = OVRLipSync.Viseme.CH };
            mCH.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthFunnel", weight = 0.6f });
            mCH.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthStretchLeft", weight = 0.5f });
            mCH.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthStretchRight", weight = 0.5f });
            mCH.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthDimpleLeft", weight = 0.2f });
            mCH.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthDimpleRight", weight = 0.2f });
            mappings.Add(mCH);
 

            var mSS = new VisemeMapping { viseme = OVRLipSync.Viseme.SS };
            mSS.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthStretchLeft", weight = 0.6f });
            mSS.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthStretchRight", weight = 0.6f });
            mSS.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthShrugLower", weight = 0.4f });
            mSS.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthDimpleLeft", weight = 0.15f });
            mSS.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthDimpleRight", weight = 0.15f });
            mappings.Add(mSS);
 

            var mnn = new VisemeMapping { viseme = OVRLipSync.Viseme.nn };
            mnn.blendShapes.Add(new BlendshapeWeight { blendShapeName = "jawOpen", weight = 0.2f });
            mnn.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthClose", weight = 0.5f });
            mappings.Add(mnn);
 

            var mRR = new VisemeMapping { viseme = OVRLipSync.Viseme.RR };
            mRR.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthPucker", weight = 0.9f });
            mRR.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthFunnel", weight = 0.3f });
            mRR.blendShapes.Add(new BlendshapeWeight { blendShapeName = "cheekPuff", weight = 0.1f });
            mappings.Add(mRR);
 

            var maa = new VisemeMapping { viseme = OVRLipSync.Viseme.aa };
            maa.blendShapes.Add(new BlendshapeWeight { blendShapeName = "jawOpen", weight = 0.85f });
            maa.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthLowerDownLeft", weight = 0.45f });
            maa.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthLowerDownRight", weight = 0.45f });
            mappings.Add(maa);
 

            var mE = new VisemeMapping { viseme = OVRLipSync.Viseme.E };
            mE.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthStretchLeft", weight = 0.85f });
            mE.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthStretchRight", weight = 0.85f });
            mE.blendShapes.Add(new BlendshapeWeight { blendShapeName = "jawOpen", weight = 0.25f });
            mappings.Add(mE);
 

            var mih = new VisemeMapping { viseme = OVRLipSync.Viseme.ih };
            mih.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthStretchLeft", weight = 0.7f });
            mih.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthStretchRight", weight = 0.7f });
            mih.blendShapes.Add(new BlendshapeWeight { blendShapeName = "jawOpen", weight = 0.15f });
            mappings.Add(mih);
 

            var moh = new VisemeMapping { viseme = OVRLipSync.Viseme.oh };
            moh.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthFunnel", weight = 0.85f });
            moh.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthPucker", weight = 0.35f });
            moh.blendShapes.Add(new BlendshapeWeight { blendShapeName = "jawOpen", weight = 0.4f });
            mappings.Add(moh);
 

            var mou = new VisemeMapping { viseme = OVRLipSync.Viseme.ou };
            mou.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthPucker", weight = 1f });
            mou.blendShapes.Add(new BlendshapeWeight { blendShapeName = "mouthFunnel", weight = 0.4f });
            mappings.Add(mou);

            Debug.Log($"[LipSync Data] ARKit Profile successfully generated with {mappings.Count} visemes.");
        }
    }
}

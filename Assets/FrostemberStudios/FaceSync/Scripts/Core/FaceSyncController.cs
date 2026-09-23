using UnityEngine;
using System.Collections;
using System.Collections.Generic;
//using Oculus.LipSync;

namespace Frostember.FaceSync
{
    [AddComponentMenu("Frostember Studios/FaceSync/FaceSync Controller")]
    public class FaceSyncController : MonoBehaviour
    {
        #region Settings Classes

        public enum LipSyncMode { Playback, LiveMicrophone }

        [System.Serializable]
        public class LipSyncSettings
        {
            public bool enabled = true;
            public LipSyncMode mode = LipSyncMode.Playback;
            public SkinnedMeshRenderer targetMesh;
            public VisemeMappingProfile profile;
            [Range(0f, 0.5f)] public float audioSyncDelay = 0.05f;

            [Header("LOD Settings")]
            public float lodMediumDistance = 15f;
            public float lodOffDistance = 35f;
            public bool frustumCulling = true;
        }

        [System.Serializable]
        public class EyeSettings
        {
            public bool enabled = true;
            public SkinnedMeshRenderer targetMesh;
            public string leftEyeName = "Eye_Blink_L";
            public string rightEyeName = "Eye_Blink_R";
            public float minBlinkInterval = 2f;
            public float maxBlinkInterval = 6f;
            public float blinkSpeed = 25f;
            public float maxBlinkWeight = 100f;

            [Header("Look Blendshapes")]
            public string eyeLookUpL = "Eye_Look_Up_L";
            public string eyeLookUpR = "Eye_Look_Up_R";
            public string eyeLookDownL = "Eye_Look_Down_L";
            public string eyeLookDownR = "Eye_Look_Down_R";
            public string eyeLookLeftL = "Eye_Look_Left_L";
            public string eyeLookLeftR = "Eye_Look_Left_R";
            public string eyeLookRightL = "Eye_Look_Right_L";
            public string eyeLookRightR = "Eye_Look_Right_R";

            [Header("Movement")]
            public bool enableSaccadic = true;
            public float saccadicInterval = 2f;
            public float saccadicStrength = 0.5f;
            public float eyeMovementSpeed = 10f;
            public bool enableMicroJitter = true;
            public float microJitterStrength = 0.05f;
        }

        [System.Serializable]
        public class HeadSettings
        {
            public bool enabled = true;
            public Transform headBone;
            public Transform neckBone;
            public float motionSpeed = 2f;
            public float speakingInfluence = 1.5f;
            public float maxYaw = 4f;
            public float maxPitch = 2.4f;
            public float maxRoll = 1.6f;
        }

        [System.Serializable]
        public class EmotionSettings
        {
            public bool enabled = true;
            public EmotionProfile profile;
            public float transitionSpeed = 8f;
            [Tooltip("Type emotion name here to test during Play mode (e.g., 'Happy')")]
            public string currentEmotion = "";
        }

        public enum BoneAxis
        {
            PositiveZ, NegativeZ,
            PositiveY, NegativeY,
            PositiveX, NegativeX
        }

        [System.Serializable]
        public class IKBone
        {
            public Transform bone;
            [Range(0f, 1f)] public float influence = 1f;
            public BoneAxis forwardAxis = BoneAxis.PositiveZ;
            [Range(0f, 180f)] public float maxAngle = 60f;
            
            [HideInInspector] public Vector3 currentLookDir;
            [HideInInspector] public Quaternion cleanLocalRotation;
            [HideInInspector] public Quaternion lastFinalLocalRotation;
            [HideInInspector] public bool initialized = false;

            public Vector3 GetForwardVector()
            {
                switch (forwardAxis)
                {
                    case BoneAxis.PositiveX: return Vector3.right;
                    case BoneAxis.NegativeX: return Vector3.left;
                    case BoneAxis.PositiveY: return Vector3.up;
                    case BoneAxis.NegativeY: return Vector3.down;
                    case BoneAxis.PositiveZ: return Vector3.forward;
                    case BoneAxis.NegativeZ: return Vector3.back;
                    default: return Vector3.forward;
                }
            }
        }

        [System.Serializable]
        public class IKSettings
        {
            public bool enabled = false;
            public Transform lookTarget;
            [Range(0f, 1f)] public float globalWeight = 1f;
            public float smoothSpeed = 5f;
            
            [Header("Bones")]
            public List<IKBone> headBones = new List<IKBone>();
            public List<IKBone> eyeBones = new List<IKBone>();
        }


        #endregion

        [Header("Modules")]
        public LipSyncSettings lipSync = new LipSyncSettings();
        public EmotionSettings emotions = new EmotionSettings();
        public EyeSettings eyes = new EyeSettings();
        public HeadSettings head = new HeadSettings();
        public IKSettings ikLook = new IKSettings();


        private OVRLipSyncContextBase lipsyncContext;
        private FaceSyncPlayer dataPlayer;
        public bool isInitialized = false;
        public bool isDrivenByTimeline = false;

        public float[] currentBlendWeights;
        private float[] targetBlendWeights;


        [HideInInspector] public int[] visemeOffsets;
        [HideInInspector] public int[] mappingIndices;
        [HideInInspector] public float[] mappingWeights;
        [HideInInspector] public int[] activeLipSyncIndices;


        [HideInInspector] public OVRLipSyncContext contextReference;
        private HashSet<int> lipsyncIndices = new HashSet<int>();
        private float currentVoiceEnergy = 0f;
        private float lastVoiceEnergy = 0f;


        private int leftEyeIdx = -1, rightEyeIdx = -1;
        private int lookUpL = -1, lookUpR = -1, lookDownL = -1, lookDownR = -1;
        private int lookLeftL = -1, lookLeftR = -1, lookRightL = -1, lookRightR = -1;
        private float currentBlinkWeight = 0f, targetBlinkWeight = 0f;
        private Vector2 currentEyeLook = Vector2.zero, targetEyeLook = Vector2.zero;
        private Vector2 currentMicroLook = Vector2.zero, targetMicroLook = Vector2.zero;


        private Quaternion cleanHeadRot, cleanNeckRot;
        private Quaternion lastFinalHeadRot, lastFinalNeckRot;
        private float noiseTimeX, noiseTimeY, noiseTimeZ;
        private Vector3 currentHeadLook = Vector3.zero, targetHeadLook = Vector3.zero;
        private float currentIKWeight = 0f;


        private struct DelayedFrame
        {
            public float timeReceived;
            public float[] visemes;
        }
        private Queue<DelayedFrame> frameBuffer = new Queue<DelayedFrame>();
        private Stack<float[]> visemePool = new Stack<float[]>();

        public float[] currentEmotionWeights;

        void Start()
        {
            InitReferences();
            if (lipSync.targetMesh != null)
            {
                currentBlendWeights = new float[lipSync.targetMesh.sharedMesh.blendShapeCount];
                targetBlendWeights = new float[lipSync.targetMesh.sharedMesh.blendShapeCount];
                currentEmotionWeights = new float[lipSync.targetMesh.sharedMesh.blendShapeCount];
                InitializeMapping();
            }


            if (eyes.enabled) InitializeEyes();
            if (head.enabled) InitializeHead();

            if (isInitialized)
            {
                StartCoroutine(BlinkRoutine());
                StartCoroutine(EyeMovementRoutine());
                StartCoroutine(MicroJitterRoutine());
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            InitReferences();
        }
#endif

        private void InitReferences()
        {
            if (lipsyncContext == null) lipsyncContext = GetComponent<OVRLipSyncContextBase>();
            if (contextReference == null) contextReference = lipsyncContext as OVRLipSyncContext;
            if (dataPlayer == null) dataPlayer = GetComponentInParent<FaceSyncPlayer>();
        }

        public void InitializeMapping()
        {
            lipsyncIndices.Clear();
            if (lipSync.profile == null || lipSync.targetMesh == null) return;


            int totalVisemes = System.Enum.GetValues(typeof(OVRLipSync.Viseme)).Length;
            List<int> flatIndices = new List<int>();
            List<float> flatWeights = new List<float>();
            visemeOffsets = new int[totalVisemes + 1];


            for (int i = 0; i < totalVisemes; i++)
            {
                visemeOffsets[i] = flatIndices.Count;
                OVRLipSync.Viseme currentViseme = (OVRLipSync.Viseme)i;
                

                VisemeMapping mapping = lipSync.profile.mappings.Find(m => m.viseme == currentViseme);
                if (mapping != null)
                {
                    foreach (var bs in mapping.blendShapes)
                    {
                        int idx = lipSync.targetMesh.sharedMesh.GetBlendShapeIndex(bs.blendShapeName);
                        if (idx >= 0)
                        {
                            flatIndices.Add(idx);
                            flatWeights.Add(bs.weight);
                            lipsyncIndices.Add(idx);
                        }
                    }
                }
            }
            visemeOffsets[totalVisemes] = flatIndices.Count;


            mappingIndices = flatIndices.ToArray();
            mappingWeights = flatWeights.ToArray();
            activeLipSyncIndices = new int[lipsyncIndices.Count];
            lipsyncIndices.CopyTo(activeLipSyncIndices);

            isInitialized = true;
        }
        private void InitializeEyes()
        {
            SkinnedMeshRenderer smr = eyes.targetMesh != null ? eyes.targetMesh : lipSync.targetMesh;
            if (smr == null) return;

            leftEyeIdx = smr.sharedMesh.GetBlendShapeIndex(eyes.leftEyeName);
            rightEyeIdx = smr.sharedMesh.GetBlendShapeIndex(eyes.rightEyeName);

            lookUpL = smr.sharedMesh.GetBlendShapeIndex(eyes.eyeLookUpL);
            lookUpR = smr.sharedMesh.GetBlendShapeIndex(eyes.eyeLookUpR);
            lookDownL = smr.sharedMesh.GetBlendShapeIndex(eyes.eyeLookDownL);
            lookDownR = smr.sharedMesh.GetBlendShapeIndex(eyes.eyeLookDownR);
            lookLeftL = smr.sharedMesh.GetBlendShapeIndex(eyes.eyeLookLeftL);
            lookLeftR = smr.sharedMesh.GetBlendShapeIndex(eyes.eyeLookLeftR);
            lookRightL = smr.sharedMesh.GetBlendShapeIndex(eyes.eyeLookRightL);
            lookRightR = smr.sharedMesh.GetBlendShapeIndex(eyes.eyeLookRightR);
        }

        private void InitializeHead()
        {
            Animator anim = GetComponentInParent<Animator>();
            if (anim != null)
            {
                if (head.headBone == null) head.headBone = anim.GetBoneTransform(HumanBodyBones.Head);
                if (head.neckBone == null) head.neckBone = anim.GetBoneTransform(HumanBodyBones.Neck);
            }

            if (head.headBone != null)
            {
                cleanHeadRot = head.headBone.localRotation;
                lastFinalHeadRot = cleanHeadRot;
                if (head.neckBone != null)
                {
                    cleanNeckRot = head.neckBone.localRotation;
                    lastFinalNeckRot = cleanNeckRot;
                }
            }
        }


    void Update()
{
    if (!isInitialized) return;


    float dist = 0f;
    if (Camera.main != null)
    {
        dist = Vector3.Distance(transform.position, Camera.main.transform.position);
        if (dist > lipSync.lodOffDistance) return;
        if (lipSync.frustumCulling && !lipSync.targetMesh.isVisible) return;
    }

    if (emotions.enabled) UpdateEmotions();

    if (isDrivenByTimeline)
    {
        frameBuffer.Clear();
    }
    else
    {
        UpdateLipSync();
    }


    if (eyes.enabled) UpdateEyes(dist);
    if (head.enabled) UpdateHeadMotion(dist);
}

        private void UpdateEmotions()
        {
            if (emotions.profile == null || lipSync.targetMesh == null || currentEmotionWeights == null) return;

            bool timelineActive = !string.IsNullOrEmpty(timelineEmotionName) && timelineEmotionWeight > 0f;
            string activeEmotion = timelineActive ? timelineEmotionName : emotions.currentEmotion;
            float activeWeight = timelineActive ? timelineEmotionWeight : 1f;

            EmotionMapping mapping = string.IsNullOrEmpty(activeEmotion) ? null : emotions.profile.GetEmotion(activeEmotion);

            float smooth = Time.deltaTime * emotions.transitionSpeed;
            float mult = emotions.profile.globalWeightMultiplier;

            for (int i = 0; i < currentEmotionWeights.Length; i++)
            {
                float targetW = 0f;
                if (mapping != null && activeWeight > 0.001f)
                {
                    foreach (var bs in mapping.blendShapes)
                    {
                        int idx = lipSync.targetMesh.sharedMesh.GetBlendShapeIndex(bs.blendShapeName);
                        if (idx == i)
                        {
                            targetW = bs.weight * mult * activeWeight;
                            break;
                        }
                    }
                }
                currentEmotionWeights[i] = Mathf.Lerp(currentEmotionWeights[i], targetW, smooth);
            }
        }

        #region Timeline Overrides

        private float timelineVoiceEnergy = 0f;
        private bool isBlinkOverridden = false;
        private bool timelineBlinkActive = true;
        private bool isEyeMoveOverridden = false;
        private bool timelineEyeMoveActive = true;
        private float timelineEyeMoveIntensity = 1f;

        public void InjectTimelineVoiceEnergy(float energy) => timelineVoiceEnergy = energy;
        public void InjectTimelineBlink(bool overrideActive, bool enabled) { isBlinkOverridden = overrideActive; timelineBlinkActive = enabled; }
        public void InjectTimelineEyeMovement(bool overrideActive, bool enabled, float intensity) { isEyeMoveOverridden = overrideActive; timelineEyeMoveActive = enabled; timelineEyeMoveIntensity = intensity; }

        private string timelineEmotionName = "";
        private float timelineEmotionWeight = 0f;
        public void InjectTimelineEmotion(string emotion, float weight) { timelineEmotionName = emotion; timelineEmotionWeight = weight; }

        public void SetEmotion(string emotionName) => emotions.currentEmotion = emotionName;
        public void ClearEmotion() => emotions.currentEmotion = "";
 
        #endregion

            private void UpdateLipSync()
        {
            if (targetBlendWeights == null) return;


            for (int i = 0; i < targetBlendWeights.Length; i++)
            {
                targetBlendWeights[i] = emotions.enabled && currentEmotionWeights != null ? currentEmotionWeights[i] : 0f;
            }

            float[] activeVisemes = null;
            bool isFromPool = false;

            if (dataPlayer != null && dataPlayer.IsPlaying)
            {
                activeVisemes = dataPlayer.CurrentFrame.visemes;
            }
            else if (lipsyncContext != null)
            {
                OVRLipSync.Frame newFrame = lipsyncContext.GetCurrentPhonemeFrame();
                if (newFrame != null)
                {

                    float[] pooledVisemes = visemePool.Count > 0 ? visemePool.Pop() : new float[newFrame.Visemes.Length];
                    System.Array.Copy(newFrame.Visemes, pooledVisemes, newFrame.Visemes.Length);
                    frameBuffer.Enqueue(new DelayedFrame { timeReceived = Time.time, visemes = pooledVisemes });
                }

                while (frameBuffer.Count > 0 && Time.time >= frameBuffer.Peek().timeReceived + lipSync.audioSyncDelay)
                {
                    DelayedFrame df = frameBuffer.Dequeue();
                    activeVisemes = df.visemes;
                    isFromPool = true;
                }
            }

            lastVoiceEnergy = currentVoiceEnergy;
            currentVoiceEnergy = 0f;

            if (activeVisemes != null)
            {
                float globalMult = lipSync.profile.globalWeightMultiplier;
                for (int i = 0; i < activeVisemes.Length; i++)
                {
                    float val = activeVisemes[i] * globalMult;


                    if (i != (int)OVRLipSync.Viseme.sil)
                    {
                        currentVoiceEnergy += activeVisemes[i];
                    }

                    if (val > 0.001f)
                    {

                        int start = visemeOffsets[i];
                        int end = visemeOffsets[i + 1];
                        for (int m = start; m < end; m++)
                        {
                            int bsIndex = mappingIndices[m];
                            float bsWeight = mappingWeights[m];
                            targetBlendWeights[bsIndex] = Mathf.Max(targetBlendWeights[bsIndex], val * bsWeight);
                        }
                    }
                }


                if (isFromPool && activeVisemes != null)
                {
                    visemePool.Push(activeVisemes);
                }

                currentVoiceEnergy = Mathf.Clamp01(currentVoiceEnergy * 1.5f);
                if (currentVoiceEnergy < 0.01f) currentVoiceEnergy = 0f;
            }

            if (lipSync.targetMesh != null)
            {
                float smooth = Time.deltaTime * lipSync.profile.smoothingSpeed;
                
                if (emotions.enabled)
                {
                    for (int i = 0; i < targetBlendWeights.Length; i++)
                    {
                        currentBlendWeights[i] = Mathf.Lerp(currentBlendWeights[i], targetBlendWeights[i], smooth);
                        lipSync.targetMesh.SetBlendShapeWeight(i, currentBlendWeights[i]);
                    }
                }
                else
                {
                    for (int i = 0; i < activeLipSyncIndices.Length; i++)
                    {
                        int idx = activeLipSyncIndices[i];
                        currentBlendWeights[idx] = Mathf.Lerp(currentBlendWeights[idx], targetBlendWeights[idx], smooth);
                        lipSync.targetMesh.SetBlendShapeWeight(idx, currentBlendWeights[idx]);
                    }
                }
            }
        }

        public bool IsEyeBlendshape(int idx)
        {
            if (!eyes.enabled) return false;
            if (idx == -1) return false;
            if (idx == leftEyeIdx || idx == rightEyeIdx) return true;
            if (idx == lookLeftL || idx == lookLeftR || idx == lookRightL || idx == lookRightR) return true;
            if (idx == lookUpL || idx == lookUpR || idx == lookDownL || idx == lookDownR) return true;
            return false;
        }

        private void UpdateEyes(float dist)
{
    if (dist > lipSync.lodMediumDistance) return;

    SkinnedMeshRenderer smr = eyes.targetMesh != null ? eyes.targetMesh : lipSync.targetMesh;
    if (smr == null) return;


    if (dataPlayer != null && dataPlayer.IsPlaying)
    {

        currentBlinkWeight = dataPlayer.CurrentFrame.blinkWeight;
        targetBlinkWeight = dataPlayer.CurrentFrame.blinkWeight;
        if (leftEyeIdx != -1) smr.SetBlendShapeWeight(leftEyeIdx, currentBlinkWeight);
        if (rightEyeIdx != -1) smr.SetBlendShapeWeight(rightEyeIdx, currentBlinkWeight);


        Vector2 finalLook = dataPlayer.CurrentFrame.eyeLook;

        float uLeft = Mathf.Clamp01(-finalLook.x) * 100f;
        float uRight = Mathf.Clamp01(finalLook.x) * 100f;
        float uDown = Mathf.Clamp01(-finalLook.y) * 100f;
        float uUp = Mathf.Clamp01(finalLook.y) * 100f;

        if (lookLeftL != -1) smr.SetBlendShapeWeight(lookLeftL, uLeft);
        if (lookLeftR != -1) smr.SetBlendShapeWeight(lookLeftR, uLeft);
        if (lookRightL != -1) smr.SetBlendShapeWeight(lookRightL, uRight);
        if (lookRightR != -1) smr.SetBlendShapeWeight(lookRightR, uRight);
        if (lookDownL != -1) smr.SetBlendShapeWeight(lookDownL, uDown);
        if (lookDownR != -1) smr.SetBlendShapeWeight(lookDownR, uDown);
        if (lookUpL != -1) smr.SetBlendShapeWeight(lookUpL, uUp);
        if (lookUpR != -1) smr.SetBlendShapeWeight(lookUpR, uUp);

        return;
    }



    currentBlinkWeight = Mathf.Lerp(currentBlinkWeight, targetBlinkWeight, Time.deltaTime * eyes.blinkSpeed);
    if (leftEyeIdx != -1) smr.SetBlendShapeWeight(leftEyeIdx, currentBlinkWeight);
    if (rightEyeIdx != -1) smr.SetBlendShapeWeight(rightEyeIdx, currentBlinkWeight);


    if (eyes.enableSaccadic || eyes.enableMicroJitter)
    {

        if (eyes.enableSaccadic)
        {
            currentEyeLook = Vector2.Lerp(currentEyeLook, targetEyeLook * eyes.saccadicStrength, Time.deltaTime * eyes.eyeMovementSpeed);
        }


        if (eyes.enableMicroJitter)
        {
            currentMicroLook = Vector2.Lerp(currentMicroLook, targetMicroLook, Time.deltaTime * (eyes.eyeMovementSpeed * 3f));
        }


        Vector2 finalLook = currentEyeLook + currentMicroLook;

        float wLeft = Mathf.Clamp01(-finalLook.x) * 100f;
        float wRight = Mathf.Clamp01(finalLook.x) * 100f;
        float wDown = Mathf.Clamp01(-finalLook.y) * 100f;
        float wUp = Mathf.Clamp01(finalLook.y) * 100f;

        if (lookLeftL != -1) smr.SetBlendShapeWeight(lookLeftL, wLeft);
        if (lookLeftR != -1) smr.SetBlendShapeWeight(lookLeftR, wLeft);
        if (lookRightL != -1) smr.SetBlendShapeWeight(lookRightL, wRight);
        if (lookRightR != -1) smr.SetBlendShapeWeight(lookRightR, wRight);
        if (lookDownL != -1) smr.SetBlendShapeWeight(lookDownL, wDown);
        if (lookDownR != -1) smr.SetBlendShapeWeight(lookDownR, wDown);
        if (lookUpL != -1) smr.SetBlendShapeWeight(lookUpL, wUp);
        if (lookUpR != -1) smr.SetBlendShapeWeight(lookUpR, wUp);
    }
}

      private void UpdateHeadMotion(float dist)
{

    if (dataPlayer != null && dataPlayer.IsPlaying)
    {
        currentHeadLook = dataPlayer.CurrentFrame.headLook;
        targetHeadLook = currentHeadLook;
        return;
    }


    float energy = currentVoiceEnergy;
    if (isDrivenByTimeline) energy = timelineVoiceEnergy;

    if (energy <= 0.001f)
    {
        targetHeadLook = Vector3.zero;
        currentHeadLook = Vector3.Lerp(currentHeadLook, Vector3.zero, Time.deltaTime * 10f);
        return;
    }


    float intensity = energy * head.speakingInfluence;
    float speed = head.motionSpeed + (energy * head.speakingInfluence);

    noiseTimeX += Time.deltaTime * speed * 0.7f;
    noiseTimeY += Time.deltaTime * speed;
    noiseTimeZ += Time.deltaTime * speed * 0.5f;
    
    targetHeadLook.x = (Mathf.PerlinNoise(noiseTimeX, 0) - 0.5f) * 2f * intensity;
    targetHeadLook.y = (Mathf.PerlinNoise(0, noiseTimeY) - 0.5f) * 2f * intensity;
    targetHeadLook.z = (Mathf.PerlinNoise(noiseTimeZ, noiseTimeZ) - 0.5f) * 2f * intensity * 0.4f + (targetHeadLook.x * 0.3f);


    currentHeadLook = Vector3.Lerp(currentHeadLook, targetHeadLook, Time.deltaTime * 5f);
    
    if (dist > lipSync.lodMediumDistance) currentHeadLook *= 0.5f;
}

        private void LateUpdate()
        {
            UpdateCleanRotations();

            if (ikLook.enabled)
            {
                RestoreIKClean(ikLook.headBones);
                RestoreIKClean(ikLook.eyeBones);
            }

            if (head.enabled && head.headBone != null) 
            {
                ApplyHeadRotation();
            }

            if (ikLook.enabled) 
            {
                ApplyIKLook();
            }

            SaveFinalRotations();
        }

        private void UpdateCleanRotations()
        {
            if (head.enabled && head.headBone != null)
            {
                if (Quaternion.Angle(head.headBone.localRotation, lastFinalHeadRot) > 0.01f) 
                    cleanHeadRot = head.headBone.localRotation;
                if (head.neckBone != null && Quaternion.Angle(head.neckBone.localRotation, lastFinalNeckRot) > 0.01f)
                    cleanNeckRot = head.neckBone.localRotation;
            }

            if (ikLook.enabled)
            {
                UpdateIKClean(ikLook.headBones);
                UpdateIKClean(ikLook.eyeBones);
            }
        }

        private void UpdateIKClean(List<IKBone> bones)
        {
            foreach(var ik in bones)
            {
                if (ik.bone == null) continue;
                if (!ik.initialized)
                {
                    ik.cleanLocalRotation = ik.bone.localRotation;
                    ik.lastFinalLocalRotation = ik.bone.localRotation;
                    ik.currentLookDir = ik.bone.rotation * ik.GetForwardVector();
                    ik.initialized = true;
                }
                else if (Quaternion.Angle(ik.bone.localRotation, ik.lastFinalLocalRotation) > 0.01f)
                {
                    ik.cleanLocalRotation = ik.bone.localRotation;
                }
            }
        }

        private void RestoreIKClean(List<IKBone> bones)
        {
            foreach(var ik in bones)
            {
                if (ik.bone == null) continue;
                ik.bone.localRotation = ik.cleanLocalRotation;
            }
        }

        private void SaveFinalRotations()
        {
            if (head.enabled && head.headBone != null)
            {
                lastFinalHeadRot = head.headBone.localRotation;
                if (head.neckBone != null) lastFinalNeckRot = head.neckBone.localRotation;
            }

            if (ikLook.enabled)
            {
                SaveIKFinal(ikLook.headBones);
                SaveIKFinal(ikLook.eyeBones);
            }
        }

        private void SaveIKFinal(List<IKBone> bones)
        {
            foreach(var ik in bones)
            {
                if (ik.bone == null) continue;
                ik.lastFinalLocalRotation = ik.bone.localRotation;
            }
        }

        private void ApplyIKLook()
        {
            float targetWeight = (ikLook.lookTarget != null) ? ikLook.globalWeight : 0f;
            currentIKWeight = Mathf.Lerp(currentIKWeight, targetWeight, Time.deltaTime * ikLook.smoothSpeed);

            if (currentIKWeight > 0.001f && ikLook.lookTarget != null)
            {
                Vector3 targetPos = ikLook.lookTarget.position;
                ApplyIKToBones(ikLook.headBones, targetPos, currentIKWeight);
                ApplyIKToBones(ikLook.eyeBones, targetPos, currentIKWeight);
            }
            else if (currentIKWeight > 0.001f) // Smoothly returning to original rotation when target is lost
            {
                ApplyIKToBones(ikLook.headBones, transform.position + transform.forward * 10f, currentIKWeight, true);
                ApplyIKToBones(ikLook.eyeBones, transform.position + transform.forward * 10f, currentIKWeight, true);
            }
        }

        private void ApplyIKToBones(List<IKBone> bones, Vector3 targetPos, float weight, bool returningToDefault = false)
        {
            foreach (var ik in bones)
            {
                if (ik.bone == null) continue;

                Vector3 currentForward = ik.bone.rotation * ik.GetForwardVector();

                Vector3 targetLookDir = currentForward;
                if (!returningToDefault)
                {
                    Vector3 diff = targetPos - ik.bone.position;
                    if (diff.sqrMagnitude > 0.001f)
                    {
                        targetLookDir = diff.normalized;
                    }
                }

                // Clamp targetLookDir to maxAngle from currentForward FIRST
                float targetAngle = Vector3.Angle(currentForward, targetLookDir);
                if (targetAngle > ik.maxAngle && targetAngle > 0.001f)
                {
                    Vector3 axis = Vector3.Cross(currentForward, targetLookDir).normalized;
                    if (axis.sqrMagnitude < 0.001f) axis = ik.bone.up; // Fallback if exactly behind
                    targetLookDir = Quaternion.AngleAxis(ik.maxAngle, axis) * currentForward;
                }

                // Slerp the current look direction towards the CLAMPED targetLookDir
                ik.currentLookDir = Vector3.Slerp(ik.currentLookDir, targetLookDir, Time.deltaTime * ikLook.smoothSpeed).normalized;

                // Calculate delta rotation from current forward to smoothed look dir
                Quaternion delta = Quaternion.FromToRotation(currentForward, ik.currentLookDir);
                Quaternion targetRot = delta * ik.bone.rotation;

                // Apply with weight
                ik.bone.rotation = Quaternion.Slerp(ik.bone.rotation, targetRot, weight * ik.influence);
            }
        }

        public void SetLookTarget(Transform target) => ikLook.lookTarget = target;
        public void ClearLookTarget() => ikLook.lookTarget = null;
        public void SetIKWeight(float weight) => ikLook.globalWeight = Mathf.Clamp01(weight);

        private void OnDrawGizmos()
        {
            if (ikLook.enabled && ikLook.lookTarget != null)
            {
                Gizmos.color = new Color(1f, 0.4f, 0f, 0.7f);
                Gizmos.DrawWireSphere(ikLook.lookTarget.position, 0.1f);
                
                Gizmos.color = new Color(1f, 0.4f, 0f, 0.3f);
                if (head.headBone != null)
                {
                    Gizmos.DrawLine(head.headBone.position, ikLook.lookTarget.position);
                }
                else
                {
                    Gizmos.DrawLine(transform.position, ikLook.lookTarget.position);
                }
            }
        }

        private void ApplyHeadRotation()
        {
            float headWeight = head.neckBone != null ? 0.6f : 1f;
            
            Quaternion headRotOffset = Quaternion.Euler(
                currentHeadLook.y * head.maxPitch * headWeight, 
                currentHeadLook.x * head.maxYaw * headWeight, 
                currentHeadLook.z * head.maxRoll * headWeight
            );

            head.headBone.localRotation = cleanHeadRot * headRotOffset;

            if (head.neckBone != null)
            {
                float neckWeight = 0.4f;
                
                Quaternion neckRotOffset = Quaternion.Euler(
                    currentHeadLook.y * head.maxPitch * neckWeight, 
                    currentHeadLook.x * head.maxYaw * neckWeight, 
                    currentHeadLook.z * head.maxRoll * neckWeight
                );

                head.neckBone.localRotation = cleanNeckRot * neckRotOffset;
            }
        }

        #region Routines

        private IEnumerator MicroJitterRoutine()
{
    while (true)
    {
        if (eyes.enableMicroJitter)
        {

            targetMicroLook = Random.insideUnitCircle * eyes.microJitterStrength;
        }
        else
        {
            targetMicroLook = Vector2.zero;
        }
        

        yield return new WaitForSeconds(Random.Range(0.3f, 0.8f));
    }
}

        private IEnumerator BlinkRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(Random.Range(eyes.minBlinkInterval, eyes.maxBlinkInterval));
                targetBlinkWeight = eyes.maxBlinkWeight;
                yield return new WaitForSeconds(0.12f);
                targetBlinkWeight = 0f;
            }
        }

      private IEnumerator EyeMovementRoutine()
{
    while (true)
    {
        if (eyes.enableSaccadic)
        {
            targetEyeLook = Random.insideUnitCircle * eyes.saccadicStrength;
        }
        yield return new WaitForSeconds(Random.Range(1f, 3f) * eyes.saccadicInterval);
    }
}

        #endregion

        #region Editor Setup (Smart Find)
#if UNITY_EDITOR
        private void Reset()
        {
            if (lipSync.profile == null)
            {
                string[] guids = UnityEditor.AssetDatabase.FindAssets("t:VisemeMappingProfile");
                if (guids.Length > 0) lipSync.profile = UnityEditor.AssetDatabase.LoadAssetAtPath<VisemeMappingProfile>(UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            if (eyes.leftEyeName == "Eye_Blink_L")
            {
                if (lipSync.targetMesh != null)
                {
                    foreach (var sm in lipSync.targetMesh.sharedMesh.GetBlendShapeIndex("Eye_Blink_L") != -1 ? new string[]{} : new string[]{"Blink", "EyeClose"})
                    {

                    }
                }
            }


            if (lipSync.targetMesh != null)
            {
                var mesh = lipSync.targetMesh.sharedMesh;
                if (mesh.GetBlendShapeIndex(eyes.eyeLookUpL) == -1) eyes.eyeLookUpL = FindBS(mesh, "LookUp_L", "EyeUpLeft", "EyeUp_L");
                if (mesh.GetBlendShapeIndex(eyes.eyeLookUpR) == -1) eyes.eyeLookUpR = FindBS(mesh, "LookUp_R", "EyeUpRight", "EyeUp_R");
                if (mesh.GetBlendShapeIndex(eyes.eyeLookDownL) == -1) eyes.eyeLookDownL = FindBS(mesh, "LookDown_L", "EyeDownLeft", "EyeDown_L");
                if (mesh.GetBlendShapeIndex(eyes.eyeLookDownR) == -1) eyes.eyeLookDownR = FindBS(mesh, "LookDown_R", "EyeDownRight", "EyeDown_R");
                if (mesh.GetBlendShapeIndex(eyes.eyeLookLeftL) == -1) eyes.eyeLookLeftL = FindBS(mesh, "LookLeft_L", "EyeLeftLeft", "EyeLeft_L");
                if (mesh.GetBlendShapeIndex(eyes.eyeLookLeftR) == -1) eyes.eyeLookLeftR = FindBS(mesh, "LookLeft_R", "EyeLeftRight", "EyeLeft_R");
                if (mesh.GetBlendShapeIndex(eyes.eyeLookRightL) == -1) eyes.eyeLookRightL = FindBS(mesh, "LookRight_L", "EyeRightLeft", "EyeRight_L");
                if (mesh.GetBlendShapeIndex(eyes.eyeLookRightR) == -1) eyes.eyeLookRightR = FindBS(mesh, "LookRight_R", "EyeRightRight", "EyeRight_R");
            }

        }

        private string FindBS(Mesh mesh, params string[] keywords)
        {
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i).ToLower();
                foreach (var k in keywords) if (name.Contains(k.ToLower())) return mesh.GetBlendShapeName(i);
            }
            return "";
        }
#endif
        #endregion
    }
}

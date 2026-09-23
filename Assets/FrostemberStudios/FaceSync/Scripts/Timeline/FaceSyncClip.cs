using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Frostember.FaceSync.Timeline
{
    [System.Serializable]
    public class FaceSyncClip : PlayableAsset, ITimelineClipAsset
    {
        [Header("Data Sources")]
        public FaceSyncAnimationAsset bakedData;

        [Header("LipSync Overrides")]
        public bool overrideLipSync;
        [Range(0f, 0.5f)] public float audioSyncDelay = 0.05f;
        public float lodMediumDistance = 15f;
        public float lodOffDistance = 35f;

        [Header("Eyes Overrides")]
        public bool overrideEyes;
        public float minBlinkInterval = 2f;
        public float maxBlinkInterval = 6f;
        public float blinkSpeed = 25f;
        public float maxBlinkWeight = 100f;
        public bool enableSaccadic = true;
        public float saccadicInterval = 2f;
        public float saccadicStrength = 0.5f;
        public float eyeMovementSpeed = 10f;
        public bool enableMicroJitter = true;
        public float microJitterStrength = 0.05f;

        [Header("Head Overrides")]
        public bool overrideHead;
        public float motionSpeed = 2f;
        public float speakingInfluence = 1.5f;
        public float maxYaw = 4f;
        public float maxPitch = 2.4f;
        public float maxRoll = 1.6f;

        public ClipCaps clipCaps => ClipCaps.Extrapolation | ClipCaps.Blending | ClipCaps.SpeedMultiplier | ClipCaps.ClipIn;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<FaceSyncBehaviour>.Create(graph);
            FaceSyncBehaviour behaviour = playable.GetBehaviour();


            behaviour.bakedData = bakedData;
            
            behaviour.overrideLipSync = overrideLipSync;
            behaviour.audioSyncDelay = audioSyncDelay;
            behaviour.lodMediumDistance = lodMediumDistance;
            behaviour.lodOffDistance = lodOffDistance;

            behaviour.overrideEyes = overrideEyes;
            behaviour.minBlinkInterval = minBlinkInterval;
            behaviour.maxBlinkInterval = maxBlinkInterval;
            behaviour.blinkSpeed = blinkSpeed;
            behaviour.maxBlinkWeight = maxBlinkWeight;
            behaviour.enableSaccadic = enableSaccadic;
            behaviour.saccadicInterval = saccadicInterval;
            behaviour.saccadicStrength = saccadicStrength;
            behaviour.eyeMovementSpeed = eyeMovementSpeed;
            behaviour.enableMicroJitter = enableMicroJitter;
            behaviour.microJitterStrength = microJitterStrength;

            behaviour.overrideHead = overrideHead;
            behaviour.motionSpeed = motionSpeed;
            behaviour.speakingInfluence = speakingInfluence;
            behaviour.maxYaw = maxYaw;
            behaviour.maxPitch = maxPitch;
            behaviour.maxRoll = maxRoll;

            return playable;
        }

        public override double duration => bakedData != null ? bakedData.length : base.duration;
    }

    
}

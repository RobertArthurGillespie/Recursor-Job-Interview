using UnityEngine;
using UnityEngine.Playables;

namespace Frostember.FaceSync.Timeline
{
    public class FaceSyncBehaviour : PlayableBehaviour
    {
        [Header("Data Sources")]
        public FaceSyncAnimationAsset bakedData;

        [Header("LipSync Overrides")]
        public bool overrideLipSync;
        public float audioSyncDelay;
        public float lodMediumDistance;
        public float lodOffDistance;

        [Header("Eyes Overrides")]
        public bool overrideEyes;
        public float minBlinkInterval;
        public float maxBlinkInterval;
        public float blinkSpeed;
        public float maxBlinkWeight;
        public bool enableSaccadic;
        public float saccadicInterval;
        public float saccadicStrength;
        public float eyeMovementSpeed;
        public bool enableMicroJitter;
        public float microJitterStrength;

        [Header("Head Overrides")]
        public bool overrideHead;
        public float motionSpeed;
        public float speakingInfluence;
        public float maxYaw;
        public float maxPitch;
        public float maxRoll;
    }
}

using UnityEngine;
using UnityEngine.Playables;
using System.Collections.Generic;
//using Oculus.LipSync;

namespace Frostember.FaceSync.Timeline
{
    public class FaceSyncTrackMixer : PlayableBehaviour
    {
        private FaceSyncController targetController;
        private float[] finalWeights;
        

        private struct OriginalSettings
        {
            public float audioSyncDelay, lodMediumDistance, lodOffDistance;
            public float minBlinkInterval, maxBlinkInterval, blinkSpeed, maxBlinkWeight;
            public bool enableSaccadic, enableMicroJitter;
            public float saccadicInterval, saccadicStrength, eyeMovementSpeed, microJitterStrength;
            public float motionSpeed, speakingInfluence, maxYaw, maxPitch, maxRoll;
        }
        private OriginalSettings defaultSettings;
        private bool isCached = false;

       public override void ProcessFrame(Playable playable, FrameData info, object playerData)
{
    FaceSyncController boundController = playerData as FaceSyncController;
    if (boundController != null && targetController == null) targetController = boundController;

    if (targetController == null || targetController.lipSync.targetMesh == null || !targetController.isInitialized) return;

    int bsCount = targetController.lipSync.targetMesh.sharedMesh.blendShapeCount;
    if (finalWeights == null || finalWeights.Length != bsCount) finalWeights = new float[bsCount];


    for (int i = 0; i < targetController.activeLipSyncIndices.Length; i++)
    {
        finalWeights[targetController.activeLipSyncIndices[i]] = 0f;
    }

    if (!isCached) CacheOriginalSettings(targetController);

    int inputCount = playable.GetInputCount();
    float totalWeight = 0f;

    float accAudioDelay = 0, accLodMed = 0, accLodOff = 0;
    float accMinBlink = 0, accMaxBlink = 0, accBlinkSpeed = 0, accMaxBlinkWeight = 0;
    float accSaccadicInt = 0, accSaccadicStr = 0, accEyeSpeed = 0, accMicroStr = 0;
    float accMotionSpeed = 0, accSpeakInf = 0, accYaw = 0, accPitch = 0, accRoll = 0;
    

    float accVoiceEnergy = 0f;
    
    bool anyLipOverride = false, anyEyeOverride = false, anyHeadOverride = false;
    bool overSaccadic = false, overMicro = false;
    bool hasBakedDataThisFrame = false;

    for (int i = 0; i < inputCount; i++)
    {
        float inputWeight = playable.GetInputWeight(i);
        if (inputWeight <= 0f) continue;

        ScriptPlayable<FaceSyncBehaviour> inputPlayable = (ScriptPlayable<FaceSyncBehaviour>)playable.GetInput(i);
        FaceSyncBehaviour input = inputPlayable.GetBehaviour();
        totalWeight += inputWeight;

        if (input.bakedData != null && input.bakedData.frames.Count > 0)
        {
            hasBakedDataThisFrame = true;
            float time = (float)inputPlayable.GetTime();
            FaceSyncAnimationFrame frameData = input.bakedData.GetFrameAtTime(time);

            float frameEnergy = 0f;

            for (int v = 0; v < frameData.visemes.Length; v++)
            {

                if (v != (int)OVRLipSync.Viseme.sil) frameEnergy += frameData.visemes[v];

                float visemeVal = frameData.visemes[v] * targetController.lipSync.profile.globalWeightMultiplier;
                if (visemeVal > 0.001f)
                {

                    int start = targetController.visemeOffsets[v];
                    int end = targetController.visemeOffsets[v + 1];
                    for (int m = start; m < end; m++)
                    {
                        int bsIndex = targetController.mappingIndices[m];
                        float bsWeight = targetController.mappingWeights[m];
                        float contribute = visemeVal * bsWeight * inputWeight;
                        finalWeights[bsIndex] = Mathf.Max(finalWeights[bsIndex], contribute);
                    }
                }
            }
            

            accVoiceEnergy += Mathf.Clamp01(frameEnergy * 1.5f) * inputWeight;
        }

        if (input.overrideLipSync)
        {
            anyLipOverride = true;
            accAudioDelay += input.audioSyncDelay * inputWeight;
            accLodMed += input.lodMediumDistance * inputWeight;
            accLodOff += input.lodOffDistance * inputWeight;
        }

        if (input.overrideEyes)
        {
            anyEyeOverride = true;
            accMinBlink += input.minBlinkInterval * inputWeight;
            accMaxBlink += input.maxBlinkInterval * inputWeight;
            accBlinkSpeed += input.blinkSpeed * inputWeight;
            accMaxBlinkWeight += input.maxBlinkWeight * inputWeight;
            accSaccadicInt += input.saccadicInterval * inputWeight;
            accSaccadicStr += input.saccadicStrength * inputWeight;
            accEyeSpeed += input.eyeMovementSpeed * inputWeight;
            accMicroStr += input.microJitterStrength * inputWeight;
            
            if (input.enableSaccadic && inputWeight > 0.5f) overSaccadic = true;
            if (input.enableMicroJitter && inputWeight > 0.5f) overMicro = true;
        }

        if (input.overrideHead)
        {
            anyHeadOverride = true;
            accMotionSpeed += input.motionSpeed * inputWeight;
            accSpeakInf += input.speakingInfluence * inputWeight;
            accYaw += input.maxYaw * inputWeight;
            accPitch += input.maxPitch * inputWeight;
            accRoll += input.maxRoll * inputWeight;
        }
    }

    if (totalWeight > 0f)
    {
        targetController.isDrivenByTimeline = true;

        targetController.InjectTimelineVoiceEnergy(accVoiceEnergy);

       if (hasBakedDataThisFrame)
{

    float smooth = Application.isPlaying ? (info.deltaTime * targetController.lipSync.profile.smoothingSpeed) : 1f;

    int limit = targetController.emotions.enabled && targetController.currentEmotionWeights != null ? targetController.lipSync.targetMesh.sharedMesh.blendShapeCount : targetController.activeLipSyncIndices.Length;

    for (int i = 0; i < limit; i++)
    {
        int idx = targetController.emotions.enabled && targetController.currentEmotionWeights != null ? i : targetController.activeLipSyncIndices[i];
        
        if (targetController.IsEyeBlendshape(idx)) continue;

        float baseWeight = targetController.emotions.enabled && targetController.currentEmotionWeights != null ? targetController.currentEmotionWeights[idx] : 0f;
        float finalW = Mathf.Max(finalWeights[idx], baseWeight);

        targetController.currentBlendWeights[idx] = Mathf.Lerp(targetController.currentBlendWeights[idx], finalW, smooth);
        
        targetController.lipSync.targetMesh.SetBlendShapeWeight(idx, targetController.currentBlendWeights[idx]);
    }
}

        if (anyLipOverride)
        {
            targetController.lipSync.audioSyncDelay = accAudioDelay / totalWeight;
            targetController.lipSync.lodMediumDistance = accLodMed / totalWeight;
            targetController.lipSync.lodOffDistance = accLodOff / totalWeight;
        }

        if (anyEyeOverride)
        {
            targetController.eyes.minBlinkInterval = accMinBlink / totalWeight;
            targetController.eyes.maxBlinkInterval = accMaxBlink / totalWeight;
            targetController.eyes.blinkSpeed = accBlinkSpeed / totalWeight;
            targetController.eyes.maxBlinkWeight = accMaxBlinkWeight / totalWeight;
            targetController.eyes.saccadicInterval = accSaccadicInt / totalWeight;
            targetController.eyes.saccadicStrength = accSaccadicStr / totalWeight;
            targetController.eyes.eyeMovementSpeed = accEyeSpeed / totalWeight;
            targetController.eyes.microJitterStrength = accMicroStr / totalWeight;
            targetController.eyes.enableSaccadic = overSaccadic;
            targetController.eyes.enableMicroJitter = overMicro;
        }

        if (anyHeadOverride)
        {
            targetController.head.motionSpeed = accMotionSpeed / totalWeight;
            targetController.head.speakingInfluence = accSpeakInf / totalWeight;
            targetController.head.maxYaw = accYaw / totalWeight;
            targetController.head.maxPitch = accPitch / totalWeight;
            targetController.head.maxRoll = accRoll / totalWeight;
        }
    }
    else
    {
        RestoreCharacter();
    }
}

        public override void OnPlayableDestroy(Playable playable)
        {
            RestoreCharacter();
            base.OnPlayableDestroy(playable);
        }

        public override void OnBehaviourPause(Playable playable, FrameData info)
        {
            RestoreCharacter();
            base.OnBehaviourPause(playable, info);
        }

        private void CacheOriginalSettings(FaceSyncController c)
        {
            if (c == null) return;
            defaultSettings = new OriginalSettings
            {
                audioSyncDelay = c.lipSync.audioSyncDelay,
                lodMediumDistance = c.lipSync.lodMediumDistance,
                lodOffDistance = c.lipSync.lodOffDistance,

                minBlinkInterval = c.eyes.minBlinkInterval,
                maxBlinkInterval = c.eyes.maxBlinkInterval,
                blinkSpeed = c.eyes.blinkSpeed,
                maxBlinkWeight = c.eyes.maxBlinkWeight,
                enableSaccadic = c.eyes.enableSaccadic,
                saccadicInterval = c.eyes.saccadicInterval,
                saccadicStrength = c.eyes.saccadicStrength,
                eyeMovementSpeed = c.eyes.eyeMovementSpeed,
                enableMicroJitter = c.eyes.enableMicroJitter,
                microJitterStrength = c.eyes.microJitterStrength,

                motionSpeed = c.head.motionSpeed,
                speakingInfluence = c.head.speakingInfluence,
                maxYaw = c.head.maxYaw,
                maxPitch = c.head.maxPitch,
                maxRoll = c.head.maxRoll
            };
            isCached = true;
        }

      private void RestoreCharacter()
{
    if (targetController == null || !isCached) return;

if (targetController.activeLipSyncIndices != null && targetController.lipSync.targetMesh != null)
{
    for (int i = 0; i < targetController.activeLipSyncIndices.Length; i++)
    {
        int idx = targetController.activeLipSyncIndices[i];
        targetController.lipSync.targetMesh.SetBlendShapeWeight(idx, 0f);
        targetController.currentBlendWeights[idx] = 0f; 
    }
}

    targetController.lipSync.audioSyncDelay = defaultSettings.audioSyncDelay;
    targetController.lipSync.lodMediumDistance = defaultSettings.lodMediumDistance;
    targetController.lipSync.lodOffDistance = defaultSettings.lodOffDistance;

    targetController.eyes.minBlinkInterval = defaultSettings.minBlinkInterval;
    targetController.eyes.maxBlinkInterval = defaultSettings.maxBlinkInterval;
    targetController.eyes.blinkSpeed = defaultSettings.blinkSpeed;
    targetController.eyes.maxBlinkWeight = defaultSettings.maxBlinkWeight;
    targetController.eyes.enableSaccadic = defaultSettings.enableSaccadic;
    targetController.eyes.saccadicInterval = defaultSettings.saccadicInterval;
    targetController.eyes.saccadicStrength = defaultSettings.saccadicStrength;
    targetController.eyes.eyeMovementSpeed = defaultSettings.eyeMovementSpeed;
    targetController.eyes.enableMicroJitter = defaultSettings.enableMicroJitter;
    targetController.eyes.microJitterStrength = defaultSettings.microJitterStrength;

    targetController.head.motionSpeed = defaultSettings.motionSpeed;
    targetController.head.speakingInfluence = defaultSettings.speakingInfluence;
    targetController.head.maxYaw = defaultSettings.maxYaw;
    targetController.head.maxPitch = defaultSettings.maxPitch;
    targetController.head.maxRoll = defaultSettings.maxRoll;

    targetController.isDrivenByTimeline = false;
    targetController.InjectTimelineVoiceEnergy(0f);
    isCached = false;
}
    }
}

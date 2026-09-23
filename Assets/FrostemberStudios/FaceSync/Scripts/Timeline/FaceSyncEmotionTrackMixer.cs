using UnityEngine;
using UnityEngine.Playables;

namespace Frostember.FaceSync.Timeline
{
    public class FaceSyncEmotionTrackMixer : PlayableBehaviour
    {
        private FaceSyncController controllerBinding;

        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            controllerBinding = playerData as FaceSyncController;
            if (controllerBinding == null) return;

            int inputCount = playable.GetInputCount();
            string blendedEmotion = "";
            float maxWeight = 0f;

            for (int i = 0; i < inputCount; i++)
            {
                float inputWeight = playable.GetInputWeight(i);
                if (inputWeight > 0.001f)
                {
                    ScriptPlayable<FaceSyncEmotionBehaviour> inputPlayable = (ScriptPlayable<FaceSyncEmotionBehaviour>)playable.GetInput(i);
                    FaceSyncEmotionBehaviour behaviour = inputPlayable.GetBehaviour();

                    // If blending, we take the emotion with the highest weight
                    if (inputWeight > maxWeight)
                    {
                        maxWeight = inputWeight;
                        blendedEmotion = behaviour.emotionName;
                    }
                }
            }

            if (maxWeight > 0f)
            {
                controllerBinding.InjectTimelineEmotion(blendedEmotion, maxWeight);
            }
            else
            {
                controllerBinding.InjectTimelineEmotion("", 0f);
            }
        }

        public override void OnPlayableDestroy(Playable playable)
        {
            if (controllerBinding != null)
            {
                controllerBinding.InjectTimelineEmotion("", 0f);
            }
        }

        public override void OnBehaviourPause(Playable playable, FrameData info)
        {
            if (controllerBinding != null)
            {
                controllerBinding.InjectTimelineEmotion("", 0f);
            }
            base.OnBehaviourPause(playable, info);
        }
    }
}

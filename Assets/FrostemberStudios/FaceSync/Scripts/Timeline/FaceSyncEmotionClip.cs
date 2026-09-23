using System;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Frostember.FaceSync.Timeline
{
    [Serializable]
    public class FaceSyncEmotionBehaviour : PlayableBehaviour
    {
        public string emotionName;
        public float weight = 1f;
    }

    [Serializable]
    public class FaceSyncEmotionClip : PlayableAsset, ITimelineClipAsset
    {
        public EmotionProfile profile;
        public FaceSyncEmotionBehaviour template = new FaceSyncEmotionBehaviour();

        public ClipCaps clipCaps
        {
            get { return ClipCaps.Blending; }
        }

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<FaceSyncEmotionBehaviour>.Create(graph, template);
            return playable;
        }
    }
}

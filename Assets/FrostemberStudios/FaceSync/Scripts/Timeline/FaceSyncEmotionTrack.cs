using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Frostember.FaceSync.Timeline
{
    [TrackColor(0.855f, 0.2f, 0.333f)]
    [TrackClipType(typeof(FaceSyncEmotionClip))]
    [TrackBindingType(typeof(FaceSyncController))]
    public class FaceSyncEmotionTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            return ScriptPlayable<FaceSyncEmotionTrackMixer>.Create(graph, inputCount);
        }
    }
}

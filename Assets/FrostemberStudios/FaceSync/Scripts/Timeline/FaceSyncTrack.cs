using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Frostember.FaceSync.Timeline
{
    [TrackColor(0.1f, 0.8f, 0.2f)]
    [TrackClipType(typeof(FaceSyncClip))]
    [TrackBindingType(typeof(FaceSyncController))]
    public class FaceSyncTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
        {
            return ScriptPlayable<FaceSyncTrackMixer>.Create(graph, inputCount);
        }

        public override void GatherProperties(PlayableDirector director, IPropertyCollector driver)
        {
            base.GatherProperties(director, driver);
        }
    }
}

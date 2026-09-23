using UnityEngine;
using System.Collections.Generic;

namespace Frostember.FaceSync
{



    [System.Serializable]
public struct FaceSyncAnimationFrame
{
    public float[] visemes;
    public Vector3 headLook;
    public float blinkWeight;
    public Vector2 eyeLook;
}





    [CreateAssetMenu(fileName = "NewFaceSyncData", menuName = "Frostember Studios/FaceSync/FaceSync Animation Asset")]
    public class FaceSyncAnimationAsset : ScriptableObject
    {
        [Tooltip("Original audio clip.")]
        public AudioClip sourceAudio;

        [Tooltip("Frames per second (Hz).")]
        public int frameRate = 60;

        [Tooltip("Total animation length in seconds.")]
        public float length;

        [Tooltip("Baked facial animation data (visemes, head, blinking).")]
        public List<FaceSyncAnimationFrame> frames = new List<FaceSyncAnimationFrame>();

        public int TotalFrames => frames.Count;




        public FaceSyncAnimationFrame GetFrameAtTime(float time)
        {
            if (frames == null || frames.Count == 0) return default;
            
            int index = Mathf.FloorToInt(time * frameRate);
            index = Mathf.Clamp(index, 0, frames.Count - 1);
            
            return frames[index];
        }
    }
}

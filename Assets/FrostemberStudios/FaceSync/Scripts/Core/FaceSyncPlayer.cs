using UnityEngine;
using System.Collections;

namespace Frostember.FaceSync
{
    [AddComponentMenu("Frostember Studios/FaceSync/FaceSync Player")]
    public class FaceSyncPlayer : MonoBehaviour
    {
        public FaceSyncAnimationAsset currentAnimation;
        public bool playOnAwake = false;
        public bool loop = false;
 
        [Header("Audio (Optional)")]
        [Tooltip("Optional audio source. If empty, animation will play silently.")]
        public AudioSource audioSource;
 
        private bool isPlaying = false;
        private float currentTime = 0f;
        private FaceSyncAnimationFrame currentFrame;
 

        public bool IsPlaying => isPlaying;
        public FaceSyncAnimationFrame CurrentFrame => currentFrame;
 
#if UNITY_EDITOR
        private void Reset()
        {
            if (audioSource == null) audioSource = GetComponent<AudioSource>();
            if (audioSource == null) audioSource = GetComponentInParent<AudioSource>();
        }
#endif

        private void Start()
        {
            if (playOnAwake && currentAnimation != null)
            {
                Play();
            }
        }

        public void Play()
        {
            if (currentAnimation == null)
            {
                Debug.LogWarning($"[FaceSync Player] Cannot start Play, 'Current Animation' is missing on object {gameObject.name}.");
                return;
            }
            Stop();
            StartCoroutine(PlaybackRoutine());
            Debug.Log($"[FaceSync Player] Starting playback: {currentAnimation.name} on object {gameObject.name}");
        }
 
        public void Stop()
        {
            if (isPlaying) Debug.Log($"[FaceSync Player] Stopping playback on object {gameObject.name}");
            StopAllCoroutines();
            isPlaying = false;
            currentTime = 0f;
            currentFrame = default;
            if (audioSource != null && audioSource.isPlaying) audioSource.Stop();
        }

       private IEnumerator PlaybackRoutine()
{
    isPlaying = true;
    currentTime = 0f;

    if (audioSource != null && currentAnimation.sourceAudio != null)
    {
        audioSource.clip = currentAnimation.sourceAudio;
        audioSource.Play();
    }

    while (currentTime < currentAnimation.length)
    {
        
        if (audioSource != null && audioSource.isPlaying)
        {
            
            currentTime = audioSource.time;
        }
        else
        {
           
            currentTime += Time.deltaTime;
        }

        currentFrame = currentAnimation.GetFrameAtTime(currentTime);
                
        yield return null;
    }

    if (loop)
    {
        Play();
    }
    else
    {
        Stop();
    }
}
    }
}

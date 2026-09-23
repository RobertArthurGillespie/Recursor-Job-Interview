#if !UNITY_WEBGL || UNITY_EDITOR
using UnityEngine;

namespace Frostember.FaceSync
{
    [RequireComponent(typeof(AudioSource))]
    [AddComponentMenu("Frostember Studios/FaceSync/Microphone Input")]
    public class Frostember_MicrophoneInput : MonoBehaviour
    {
        [Header("Microphone Selection")]
        [Tooltip("Exact name of the recording device. If empty, uses the system default.")]
        public string selectedDeviceName = "";
        
        [Tooltip("Automatically start listening after scene loads.")]
        public bool startOnAwake = true;

        [Header("Audio Output")]
        [Tooltip("Automatically route microphone voice to AudioSource (so we can hear ourselves). Needs to be OFF for NPC LipSync!")]
        public bool outputToAudioSource = false;

        [Tooltip("Mute the AudioSource (character won't speak aloud from speakers, but LipSync will still work). Recommended for VTubing and recording.")]
        public bool muteAudioOutput = true;

        private AudioSource audioSource;
        private bool isRecording = false;

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
        }

        private void Start()
        {
            if (startOnAwake)
            {
                StartMicrophone();
            }
        }

        public void StartMicrophone()
        {
            if (isRecording) return;


            if (Microphone.devices.Length == 0)
            {
                Debug.LogError("[FaceSync Microphone] No microphone device found in the system!");
                return;
            }


            string device = string.IsNullOrEmpty(selectedDeviceName) ? Microphone.devices[0] : selectedDeviceName;
            if (string.IsNullOrEmpty(selectedDeviceName) || !System.Array.Exists(Microphone.devices, element => element == device))
            {
                device = Microphone.devices[0];
                Debug.Log($"[FaceSync Microphone] Using default microphone for Live VTubing: {device}");
            }

            Debug.Log($"[FaceSync Microphone] Starting recording from device: {device}");



            audioSource.clip = Microphone.Start(device, true, 1, 48000);
            audioSource.loop = true;




            audioSource.mute = false;


            while (!(Microphone.GetPosition(device) > 0)) { }
            



            audioSource.Play();
            isRecording = true;
        }

        public void StopMicrophone()
        {
            if (!isRecording) return;

            string device = selectedDeviceName;
            if (string.IsNullOrEmpty(device) || !System.Array.Exists(Microphone.devices, element => element == device))
            {
                device = Microphone.devices[0];
            }

            Microphone.End(device);
            audioSource.Stop();
            audioSource.clip = null;
            isRecording = false;
        }

        private void OnDisable()
        {
            StopMicrophone();
        }
    }
}
#endif
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Frostember.FaceSync;
//using Oculus.LipSync;

namespace Frostember.FaceSync.EditorTools
{
    public static class FaceSyncBaker
    {
        public static AnimationClip BakeAudioToClip(AudioClip audio, FaceSyncController controller, string relativeSMRPath = "")
        {
            if (audio == null || controller == null || controller.lipSync.profile == null) return null;

            VisemeMappingProfile profile = controller.lipSync.profile;

            Animator rootAnimator = controller.GetComponentInParent<Animator>();
            if (rootAnimator == null) rootAnimator = controller.GetComponentInChildren<Animator>();
            GameObject rootObject = (rootAnimator != null) ? rootAnimator.gameObject : controller.gameObject;

            if (string.IsNullOrEmpty(relativeSMRPath) && controller.lipSync.targetMesh != null)
            {
                relativeSMRPath = GetGameObjectPath(controller.lipSync.targetMesh.gameObject, rootObject);
            }
            if (OVRLipSync.IsInitialized() != OVRLipSync.Result.Success)
            {
                OVRLipSync.Initialize();
            }

            uint context = 0;
            OVRLipSync.CreateContext(ref context, OVRLipSync.ContextProviders.Enhanced, 0, true);

            int sampleCount = audio.samples;
            int channels = audio.channels;
            int freq = audio.frequency;
            float[] samples = new float[sampleCount * channels];
            audio.GetData(samples, 0);

            AnimationClip clip = new AnimationClip();
            clip.frameRate = 60f;
            
            Dictionary<string, Dictionary<string, AnimationCurve>> pathCurves = new Dictionary<string, Dictionary<string, AnimationCurve>>();
            
            var headSettings = controller.head;
            AnimationCurve headRotX = new AnimationCurve();
            AnimationCurve headRotY = new AnimationCurve();
            AnimationCurve headRotZ = new AnimationCurve();
            AnimationCurve headRotW = new AnimationCurve();
            string headPath = "";
            if (headSettings.enabled && headSettings.headBone != null)
            {
                headPath = GetGameObjectPath(headSettings.headBone.gameObject, rootObject);
                headRotX.AddKey(0, 0); headRotY.AddKey(0, 0); headRotZ.AddKey(0, 0); headRotW.AddKey(0, 1);
            }

            var eyeSettings = controller.eyes;
            string eyeMeshPath = relativeSMRPath;
            if (eyeSettings.enabled)
            {
                SkinnedMeshRenderer eyeMesh = eyeSettings.targetMesh != null ? eyeSettings.targetMesh : controller.lipSync.targetMesh;
                if (eyeMesh != null)
                {
                    eyeMeshPath = GetGameObjectPath(eyeMesh.gameObject, rootObject);
                }
            }

            foreach (var mapping in profile.mappings)
            {
                foreach (var bs in mapping.blendShapes)
                {
                    AddPathBSKey(pathCurves, relativeSMRPath, bs.blendShapeName, 0f, 0f);
                }
            }
            if (eyeSettings.enabled)
            {
                AddPathBSKey(pathCurves, eyeMeshPath, eyeSettings.leftEyeName, 0f, 0f);
                AddPathBSKey(pathCurves, eyeMeshPath, eyeSettings.rightEyeName, 0f, 0f);
            }

             int stepSize = freq / 60;
            int windowSize = 1024; 
            float deltaTime = 1f / 60f;
            
            Dictionary<string, float> currentBSWeights = new Dictionary<string, float>();
            
            foreach (var m in profile.mappings) foreach (var b in m.blendShapes) currentBSWeights[b.blendShapeName] = 0f;

            OVRLipSync.Frame ovrFrame = new OVRLipSync.Frame();
            int totalFrames = sampleCount / stepSize;

            float nextBlinkTime = Random.Range(1f, 3f);
            float blinkTarget = 0f;
            float currentBlink = 0f;

            for (int f = 0; f < totalFrames; f++)
            {
                int centerSample = f * stepSize;
                int startSample = centerSample - (windowSize / 2);
                
               
                float[] windowBuffer = new float[windowSize * channels];
                for (int s = 0; s < windowSize; s++)
                {
                    int actualSampleIdx = startSample + s;
                    if (actualSampleIdx >= 0 && actualSampleIdx < sampleCount)
                    {
                        for (int c = 0; c < channels; c++)
                            windowBuffer[s * channels + c] = samples[actualSampleIdx * channels + c];
                    }
                }

                OVRLipSync.ProcessFrame(context, windowBuffer, ovrFrame, channels == 2);

                float time = f * deltaTime;

                Dictionary<string, float> targetWeights = new Dictionary<string, float>();
                float totalEnergy = 0f;

                for (int v = 0; v < ovrFrame.Visemes.Length; v++)
                {
                    float val = ovrFrame.Visemes[v] * profile.globalWeightMultiplier;
                    totalEnergy += ovrFrame.Visemes[v];
                    if (val < 0.001f) continue;

                    var mapping = profile.mappings.Find(m => m.viseme == (OVRLipSync.Viseme)v);
                    if (mapping != null)
                    {
                        foreach (var bs in mapping.blendShapes)
                        {
                            if (!targetWeights.ContainsKey(bs.blendShapeName)) targetWeights[bs.blendShapeName] = 0f;
                            targetWeights[bs.blendShapeName] = Mathf.Max(targetWeights[bs.blendShapeName], val * bs.weight);
                        }
                    }
                }

                foreach (var mapping in profile.mappings)
                {
                    foreach (var bs in mapping.blendShapes)
                    {
                        string bName = bs.blendShapeName;
                        float tWeight = targetWeights.ContainsKey(bName) ? targetWeights[bName] : 0f;
                        currentBSWeights[bName] = Mathf.Lerp(currentBSWeights[bName], tWeight, deltaTime * profile.smoothingSpeed);
                        AddPathBSKey(pathCurves, relativeSMRPath, bName, time, currentBSWeights[bName]);
                    }
                }

                if (headSettings.enabled && !string.IsNullOrEmpty(headPath))
                {
                    float energy = Mathf.Clamp01(totalEnergy * 1.5f);
                    float noiseX = (Mathf.PerlinNoise(time * 2f, 0f) - 0.5f) * headSettings.maxPitch * 1.5f * headSettings.speakingInfluence * energy;
                    float noiseY = (Mathf.PerlinNoise(0f, time * 2f) - 0.5f) * headSettings.maxYaw * 1.5f * headSettings.speakingInfluence * energy;
                    
                    Quaternion rot = Quaternion.Euler(noiseX, noiseY, 0f);
                    headRotX.AddKey(time, rot.x);
                    headRotY.AddKey(time, rot.y);
                    headRotZ.AddKey(time, rot.z);
                    headRotW.AddKey(time, rot.w);
                }

                if (eyeSettings.enabled)
                {
                    if (time >= nextBlinkTime)
                    {
                        blinkTarget = eyeSettings.maxBlinkWeight;
                        if (time >= nextBlinkTime + 0.12f)
                        {
                            blinkTarget = 0f;
                            if (time >= nextBlinkTime + 0.25f)
                                nextBlinkTime = time + Random.Range(eyeSettings.minBlinkInterval, eyeSettings.maxBlinkInterval);
                        }
                    }
                    currentBlink = Mathf.Lerp(currentBlink, blinkTarget, deltaTime * eyeSettings.blinkSpeed);
                    AddPathBSKey(pathCurves, eyeMeshPath, eyeSettings.leftEyeName, time, currentBlink);
                    AddPathBSKey(pathCurves, eyeMeshPath, eyeSettings.rightEyeName, time, currentBlink);
                }

                if (f % 100 == 0) EditorUtility.DisplayProgressBar("Baking Animation", $"Full Analysis: {audio.name}", (float)f / totalFrames);
            }

            foreach (var pathKvp in pathCurves)
            {
                foreach (var bsKvp in pathKvp.Value)
                {
                    bsKvp.Value.AddKey(audio.length, 0f);
                    clip.SetCurve(pathKvp.Key, typeof(SkinnedMeshRenderer), "blendShape." + bsKvp.Key, bsKvp.Value);
                }
            }

            if (!string.IsNullOrEmpty(headPath))
            {
                headRotX.AddKey(audio.length, 0); headRotY.AddKey(audio.length, 0); headRotZ.AddKey(audio.length, 0); headRotW.AddKey(audio.length, 1);
                clip.SetCurve(headPath, typeof(Transform), "m_LocalRotation.x", headRotX);
                clip.SetCurve(headPath, typeof(Transform), "m_LocalRotation.y", headRotY);
                clip.SetCurve(headPath, typeof(Transform), "m_LocalRotation.z", headRotZ);
                clip.SetCurve(headPath, typeof(Transform), "m_LocalRotation.w", headRotW);
            }

            OVRLipSync.DestroyContext(context);
            EditorUtility.ClearProgressBar();
            
            Debug.Log($"[FaceSync Baker] SUCCESS! Animation baked.\nMesh Path: '{relativeSMRPath}'\nHead Path: '{headPath}'");

            return clip;
        }

        public static FaceSyncAnimationAsset BakeAudioToDataAsset(AudioClip audio, FaceSyncController controller)
{
    if (audio == null || controller == null || controller.lipSync.profile == null) return null;

    if (OVRLipSync.IsInitialized() != OVRLipSync.Result.Success) OVRLipSync.Initialize();

    uint lipsyncContext = 0;
    OVRLipSync.CreateContext(ref lipsyncContext, OVRLipSync.ContextProviders.Enhanced, 0, true);

    int sampleCount = audio.samples;
    int channels = audio.channels;
    int freq = audio.frequency;
    float[] samples = new float[sampleCount * channels];
    audio.GetData(samples, 0);

    FaceSyncAnimationAsset asset = ScriptableObject.CreateInstance<FaceSyncAnimationAsset>();
    asset.sourceAudio = audio;
    asset.frameRate = 60;
    asset.length = audio.length;

    int stepSize = freq / 60;
    int windowSize = 1024;
    float deltaTime = 1f / 60f;

    OVRLipSync.Frame ovrFrame = new OVRLipSync.Frame();
    int totalFrames = (int)(audio.length * 60f);

    var headSettings = controller.head;
    var eyeSettings = controller.eyes;

    float headNoiseTimeX = 0f, headNoiseTimeY = 0f, headNoiseTimeZ = 0f;
    Vector3 currentHeadLook = Vector3.zero, targetHeadLook = Vector3.zero;

    float nextBlinkTime = Random.Range(eyeSettings.minBlinkInterval, eyeSettings.maxBlinkInterval);
    float blinkTarget = 0f, currentBlink = 0f;
    int blinkPhase = 0; 

    float nextSaccadicTime = Random.Range(1f, 3f) * eyeSettings.saccadicInterval;
    Vector2 currentEyeLook = Vector2.zero, targetEyeLook = Vector2.zero;

    float nextMicroTime = Random.Range(0.3f, 0.8f);
    Vector2 currentMicroLook = Vector2.zero, targetMicroLook = Vector2.zero;

    for (int f = 0; f < totalFrames; f++)
    {
        int centerSample = f * stepSize;
        int startSample = centerSample - (windowSize / 2);
        float time = f * deltaTime;

        float[] windowBuffer = new float[windowSize * channels];
        for (int s = 0; s < windowSize; s++)
        {
            int actualSampleIdx = startSample + s;
            if (actualSampleIdx >= 0 && actualSampleIdx < sampleCount)
            {
                for (int c = 0; c < channels; c++)
                    windowBuffer[s * channels + c] = samples[actualSampleIdx * channels + c];
            }
        }

        OVRLipSync.ProcessFrame(lipsyncContext, windowBuffer, ovrFrame, channels == 2);

        FaceSyncAnimationFrame dataFrame = new FaceSyncAnimationFrame();
        dataFrame.visemes = new float[ovrFrame.Visemes.Length];
        System.Array.Copy(ovrFrame.Visemes, dataFrame.visemes, ovrFrame.Visemes.Length);

        float voiceEnergy = 0f;
        for (int v = 0; v < ovrFrame.Visemes.Length; v++)
        {
            if (v != (int)OVRLipSync.Viseme.sil) voiceEnergy += ovrFrame.Visemes[v];
        }
        voiceEnergy = Mathf.Clamp01(voiceEnergy * 1.5f);
        if (voiceEnergy < 0.01f) voiceEnergy = 0f;

        if (headSettings.enabled)
        {
            if (voiceEnergy <= 0.001f)
            {
                targetHeadLook = Vector3.zero;
                currentHeadLook = Vector3.Lerp(currentHeadLook, Vector3.zero, deltaTime * 10f);
            }
            else
            {
                float intensity = voiceEnergy * headSettings.speakingInfluence;
                float speed = headSettings.motionSpeed + (voiceEnergy * headSettings.speakingInfluence);

                headNoiseTimeX += deltaTime * speed * 0.7f;
                headNoiseTimeY += deltaTime * speed;
                headNoiseTimeZ += deltaTime * speed * 0.5f;

                targetHeadLook.x = (Mathf.PerlinNoise(headNoiseTimeX, 0) - 0.5f) * 2f * intensity;
                targetHeadLook.y = (Mathf.PerlinNoise(0, headNoiseTimeY) - 0.5f) * 2f * intensity;
                targetHeadLook.z = (Mathf.PerlinNoise(headNoiseTimeZ, headNoiseTimeZ) - 0.5f) * 2f * intensity * 0.4f + (targetHeadLook.x * 0.3f);

                currentHeadLook = Vector3.Lerp(currentHeadLook, targetHeadLook, deltaTime * 5f);
            }
            dataFrame.headLook = currentHeadLook;
        }

        if (eyeSettings.enabled)
        {
            if (time >= nextBlinkTime)
            {
                if (blinkPhase == 0) 
                {
                    blinkTarget = eyeSettings.maxBlinkWeight;
                    blinkPhase = 1;
                }
                else if (time >= nextBlinkTime + 0.12f && blinkPhase == 1) 
                {
                    blinkTarget = 0f;
                    blinkPhase = 2;
                }
                else if (time >= nextBlinkTime + 0.25f && blinkPhase == 2) 
                {
                    nextBlinkTime = time + Random.Range(eyeSettings.minBlinkInterval, eyeSettings.maxBlinkInterval);
                    blinkPhase = 0;
                }
            }
            currentBlink = Mathf.Lerp(currentBlink, blinkTarget, deltaTime * eyeSettings.blinkSpeed);
            dataFrame.blinkWeight = currentBlink;

            if (eyeSettings.enableSaccadic)
            {
                if (time >= nextSaccadicTime)
                {
                    targetEyeLook = Random.insideUnitCircle * eyeSettings.saccadicStrength;
                    nextSaccadicTime = time + (Random.Range(1f, 3f) * eyeSettings.saccadicInterval);
                }
                currentEyeLook = Vector2.Lerp(currentEyeLook, targetEyeLook, deltaTime * eyeSettings.eyeMovementSpeed);
            }

            if (eyeSettings.enableMicroJitter)
            {
                if (time >= nextMicroTime)
                {
                    targetMicroLook = Random.insideUnitCircle * eyeSettings.microJitterStrength;
                    nextMicroTime = time + Random.Range(0.3f, 0.8f);
                }
                currentMicroLook = Vector2.Lerp(currentMicroLook, targetMicroLook, deltaTime * (eyeSettings.eyeMovementSpeed * 3f));
            }

            dataFrame.eyeLook = currentEyeLook + currentMicroLook;
        }

        asset.frames.Add(dataFrame);

        if (f % 100 == 0) EditorUtility.DisplayProgressBar("Baking Data Asset", $"Simulating: {audio.name}", (float)f / totalFrames);
    }

    OVRLipSync.DestroyContext(lipsyncContext);
    EditorUtility.ClearProgressBar();

    return asset;
}

        private static void AddPathBSKey(Dictionary<string, Dictionary<string, AnimationCurve>> curves, string path, string name, float time, float val)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!curves.ContainsKey(path)) curves[path] = new Dictionary<string, AnimationCurve>();
            if (!curves[path].ContainsKey(name)) curves[path][name] = new AnimationCurve();
            curves[path][name].AddKey(time, val);
        }

        private static string GetGameObjectPath(GameObject obj, GameObject root)
        {
            if (obj == root) return "";
            string path = obj.name;
            Transform parent = obj.transform.parent;
            while (parent != null && parent.gameObject != root)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        public static void SaveClip(AnimationClip clip, string audioName)
        {
            string path = EditorUtility.SaveFilePanelInProject("Save Animation Clip", "LipSync_" + audioName, "anim", "Select folder.");
            if (!string.IsNullOrEmpty(path))
            {
                AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssets();
            }
        }

        public static void SaveFaceSyncDataAsset(FaceSyncAnimationAsset asset, string audioName)
        {
            string path = EditorUtility.SaveFilePanelInProject("Save FaceSync Data", "FaceSyncData_" + audioName, "asset", "Select folder to save data.");
            if (!string.IsNullOrEmpty(path))
            {
                AssetDatabase.CreateAsset(asset, path);
                AssetDatabase.SaveAssets();
                Debug.Log($"[FaceSync Baker] Data Asset saved to: {path}");
            }
        }
    }
}

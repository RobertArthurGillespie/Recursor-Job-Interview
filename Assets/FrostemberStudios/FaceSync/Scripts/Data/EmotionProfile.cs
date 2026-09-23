using UnityEngine;
using System.Collections.Generic;

namespace Frostember.FaceSync
{
    [System.Serializable]
    public class EmotionMapping
    {
        [Tooltip("Name of the emotion, e.g., 'Happy', 'Sad', 'Angry'")]
        public string emotionName;
        
        [Tooltip("List of blend shapes affected by this emotion")]
        public List<BlendshapeWeight> blendShapes = new List<BlendshapeWeight>();
    }

    [CreateAssetMenu(fileName = "NewEmotionProfile", menuName = "Frostember Studios/FaceSync/Emotion Profile")]
    public class EmotionProfile : ScriptableObject
    {
#if UNITY_EDITOR
        [Header("Editor Setup")]
        [Tooltip("Editor Only: Drag and drop the .FBX model or Prefab of your character to automatically find the face mesh.")]
        public GameObject editorReferencePrefab;
#endif
        [Header("Global Settings")]
        [Tooltip("Multiplier for all blendshapes in this profile.")]
        public float globalWeightMultiplier = 100f; 

        [Header("Emotion Mappings")]
        public List<EmotionMapping> emotions = new List<EmotionMapping>();

        public EmotionMapping GetEmotion(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return emotions.Find(e => e.emotionName.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        }
    }
}

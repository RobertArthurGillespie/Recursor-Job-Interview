using UnityEngine;

namespace Frostember.FaceSync.Demo
{
    [AddComponentMenu("Frostember Studios/FaceSync/Demo/Target Mover")]
    public class FaceSyncTargetMover : MonoBehaviour
    {
        [Header("Movement Settings")]
        public float speed = 2f;
        public float radius = 2f;
        public float heightOffset = 0f;

        private Vector3 startPos;
        private float angle = 0f;

        void Start()
        {
            startPos = transform.position;
        }

        void Update()
        {
            angle += speed * Time.deltaTime;
            
            float x = Mathf.Cos(angle) * radius;
            float y = Mathf.Sin(angle) * radius;
            
            transform.position = startPos + new Vector3(x, y + heightOffset, 0);
        }
    }
}

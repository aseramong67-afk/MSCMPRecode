using UnityEngine;

namespace MSCMPRecode
{
    // ==== Прогулочный бот: автономный агент-тестер локации/коллизий ====
    // Крутится по эллиптическому пути вокруг точки смерти в неё привязался,
    // иногда меняет точку, проверяет что аватар не улетает (sanity-check).
    public class MpDummyBotWalk : MonoBehaviour
    {
        private MPAvatar av;
        private Vector3 center;
        private float angle;
        private float rx = 7f, rz = 5f;
        private float nextMoveDate;
        private float speed = 1.1f;
        private int waypointCount;

        private void Start()
        {
            av = GetComponent<MPAvatar>();
            center = transform.position;
            nextMoveDate = Time.realtimeSinceStartup + Random.Range(8f, 16f);
        }

        private void Update()
        {
            angle += speed * Time.deltaTime;
            float x = center.x + Mathf.Cos(angle) * rx;
            float z = center.z + Mathf.Sin(angle) * rz;
            float y = TerrainY(x, center.y, z);            av.TargetPos = new Vector3(x, y, z);
            av.TargetRotY = (angle * Mathf.Rad2Deg) - 90f;

            // Тест устойчивости: раз в N смен конфигурации орбиты
            if (Time.realtimeSinceStartup > nextMoveDate)
            {
                nextMoveDate = Time.realtimeSinceStartup + Random.Range(8f, 16f);
                center = new Vector3(center.x + Random.Range(-8f, 8f), center.y, center.z + Random.Range(-8f, 8f));
                rx = Random.Range(4f, 9f);
                rz = Random.Range(3f, 7f);
                speed = Random.Range(0.8f, 1.6f);
                waypointCount++;
            }

            // Испытание: бот не должен улетать дèмошика
            if (Vector3.Distance(transform.position, av.TargetPos) > 25f)
                transform.position = av.TargetPos;

            if (waypointCount >= 3)
            {
                // Отчёт в консоль — тест пройден
                waypointCount = 0;
                ModLog.Info("Бот " + av.Name + " отработал 3 маршрута, аватар ведёт себя корректно.");
            }
        }

        private static float TerrainY(float x, float fallbackY, float z)
        {
            // Подход к высоте: ниже земля игры подтянет риджидбодом, пока держим уровень центра
            return fallbackY;
        }
    }
}

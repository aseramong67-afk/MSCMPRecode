using System.Collections.Generic;
using UnityEngine;

namespace MSCMPRecode
{
    // ==== Аватар удалённого игрока/бота: капсула + подпись имени ====
    public class MPAvatar : MonoBehaviour
    {
        public string Name = "Игрок";
        public Vector3 TargetPos;
        public float TargetRotY;
        public float LerpSpeed = 10f;
        public float LastSeen;
        public bool InCar;        // сидит в машине (видно по иконке в подписи)
        public bool Smoking;      // курит
        public bool Drinking;     // пьёт воду
        public float WheelAngle;  // угол руля (для будущего совмещения с машинами)
        internal short pid;

        // Привязка к машине: аватар стоит у левой двери, машина несёт его
        public Transform AnchorCar;
        public Vector3 AnchorOffset = new Vector3(-1.8f, 0.7f, 0.2f);

        private static readonly Dictionary<int, Material> matCache = new Dictionary<int, Material>();
        private Renderer bodyR, headR;
        private TextMesh labelTm;

        private void Start()
        {
            bodyR = transform.Find("body") != null ? transform.Find("body").GetComponent<Renderer>() : null;
            headR = transform.Find("head") != null ? transform.Find("head").GetComponent<Renderer>() : null;
            labelTm = transform.Find("label") != null ? transform.Find("label").GetComponent<TextMesh>() : null;
        }

        public static MPAvatar Create(short pid)
        {
            GameObject root = new GameObject("MPAvatar_" + pid);
            MPAvatar av = root.AddComponent<MPAvatar>();
            av.pid = pid;

            // Капсула в роли "тела"
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1.0f, 0f);
            body.transform.localScale = new Vector3(1.0f, 1.0f, 1.0f);
            Renderer r = body.GetComponent<Renderer>();
            r.material = CachedMat(ColorForPid(pid));
            body.layer = 0;
            Collider cb = body.GetComponent<Collider>();
            if (cb != null) GameObject.Destroy(cb); // без коллайдеров — не мешает физике игры

            // Голова-сфера
            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "head";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, 2.1f, 0f);
            head.transform.localScale = new Vector3(0.55f, 0.55f, 0.55f);
            Renderer rh = head.GetComponent<Renderer>();
            rh.material = CachedMat(ColorForPid(pid));
            head.layer = 0;
            Collider ch = head.GetComponent<Collider>();
            if (ch != null) GameObject.Destroy(ch);

            // 3D-подпись с ником
            GameObject label = new GameObject("label");
            label.transform.SetParent(root.transform, false);
            label.transform.localPosition = new Vector3(0f, 2.9f, 0f);
            TextMesh tm = label.AddComponent<TextMesh>();
            tm.fontSize = 48;
            tm.characterSize = 0.25f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.white;
            label.AddComponent<MeshRenderer>();

            // Скрипт подставляет текст подписи (Name задаётся сразу после Create)
            MpAvatarLabel lbl = label.AddComponent<MpAvatarLabel>();
            lbl.avatar = av;

            return av;
        }

        public static Color ColorForPid(short pid)
        {
            float h = ((pid % 12) / 12f);
            if (h < 0.01f) return new Color(1f, 0.45f, 0.35f);
            return ColorFromHue(h);
        }

        // Кэш материалов — без утечки VRAM на каждого нового аватара
        public static Material CachedMat(Color c)
        {
            int key = ((int)(c.g * 255) << 8) | (int)(c.b * 255);
            Material m;
            if (!matCache.TryGetValue(key, out m))
            {
                Shader sh = Shader.Find("Diffuse");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
                m = new Material(sh);
                m.color = c;
                matCache[key] = m;
            }
            return m;
        }

        private static Color ColorFromHue(float hue)
        {
            // простое HSB->RGB, S=0.85, V=1.0
            float s = 0.85f, v = 1.0f;
            float f, p, q, t;
            int i = (int)(hue * 6f) % 6;
            f = hue * 6f - i;
            p = v * (1 - s);
            q = v * (1 - f * s);
            t = v * (1 - (1 - f) * s);
            switch (i)
            {
                case 0: return new Color(v, t, p);
                case 1: return new Color(q, v, p);
                case 2: return new Color(p, v, t);
                case 3: return new Color(p, q, v);
                case 4: return new Color(t, p, v);
                default: return new Color(v, p, q);
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (AnchorCar != null)
            {
                // Сидит в машине: держим аватар у двери/в салоне, машина его ведёт
                Vector3 spot = AnchorCar.TransformPoint(AnchorOffset);
                transform.position = Vector3.MoveTowards(transform.position, spot, LerpSpeed * dt);
                Vector3 reu = AnchorCar.eulerAngles;
                Vector3 eu = transform.eulerAngles;
                eu.y = Mathf.LerpAngle(eu.y, reu.y, Mathf.Clamp(1f - dt * LerpSpeed, 0.05f, 1f));
                transform.eulerAngles = eu;
            }
            else
            {
                transform.position = Vector3.MoveTowards(transform.position, TargetPos, LerpSpeed * dt);
                Vector3 eu = transform.eulerAngles;
                eu.y = Mathf.LerpAngle(eu.y, TargetRotY, Mathf.Clamp(1f - dt * LerpSpeed, 0.05f, 1f));
                transform.eulerAngles = eu;
            }

            // Визуальные статусы: в машине — "полусидим" (тело приземляется),
            // курение/питьё отражаем в подписи
            if (bodyR != null)
            {
                Vector3 bs = bodyR.transform.localScale;
                float targetScaleY = InCar ? 0.55f : 1f;
                if (Mathf.Abs(bs.y - targetScaleY) > 0.01f)
                {
                    bs.y = Mathf.Lerp(bs.y, targetScaleY, 0.2f);
                    bodyR.transform.localScale = bs;
                }
            }

            if (labelTm != null)
            {
                string icons = "";
                if (InCar) icons += "[в машине] ";
                if (Smoking) icons += "[курит] ";
                if (Drinking) icons += "[пьёт] ";
                string now = Name + (icons.Length > 0 ? (" " + icons) : "");
                if (labelTm.text != now) labelTm.text = now;
            }
        }
    }

    public class MpAvatarLabel : MonoBehaviour
    {
        public MPAvatar avatar;

        private void Update()
        {
            Billboard();
        }

        private void Billboard()
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 d = cam.transform.position - transform.position;
                d.y = 0;
                if (d.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.LookRotation(d.normalized);
            }
        }
    }
}

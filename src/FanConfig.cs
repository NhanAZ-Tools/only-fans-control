using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace OnlyFansControl
{
    public sealed class CurvePoint
    {
        public int temperature_c { get; set; }
        public string level { get; set; }
        internal int NumericLevel { get { return ParseLevel(level); } }
        internal static int ParseLevel(string value)
        {
            int number;
            if (string.Equals(value, "Max", StringComparison.OrdinalIgnoreCase)) return 8;
            if (int.TryParse(value, out number) && number >= 1 && number <= 7) return number;
            throw new ArgumentException("Fan level must be 1 through 7 or Max.");
        }
    }

    public sealed class FanConfig
    {
        public int schema_version { get; set; }
        public int poll_interval_ms { get; set; }
        public int hysteresis_c { get; set; }
        public int critical_temperature_c { get; set; }
        public List<CurvePoint> curve { get; set; }
        public Dictionary<string, List<CurvePoint>> profile_curves { get; set; }
        internal static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "only_fans_config.json");

        internal static FanConfig Load(string path, EcProfile profile = null)
        { return Parse(File.ReadAllText(path), profile); }

        internal static FanConfig Parse(string text, EcProfile profile = null)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            FanConfig value = serializer.Deserialize<FanConfig>(text);
            if (value != null && value.schema_version == 0)
            {
                Dictionary<string, object> root = serializer.DeserializeObject(text) as Dictionary<string, object>;
                object policyValue;
                if (root != null && root.TryGetValue("fan_policy", out policyValue))
                {
                    Dictionary<string, object> policy = policyValue as Dictionary<string, object>;
                    if (policy == null) throw new ArgumentException("Invalid fan_policy in the earlier configuration.");
                    value = new FanConfig { schema_version = 1, poll_interval_ms = Convert.ToInt32(policy["poll_ms"]),
                        hysteresis_c = Convert.ToInt32(policy["hysteresis_c"]), critical_temperature_c = Convert.ToInt32(policy["failsafe_temp_c"]), curve = new List<CurvePoint>() };
                    foreach (object item in (object[])policy["smart_curve"])
                    {
                        Dictionary<string, object> point = item as Dictionary<string, object>;
                        if (point == null) throw new ArgumentException("Invalid point in the earlier configuration.");
                        string level = Convert.ToString(point["level"]);
                        value.curve.Add(new CurvePoint { temperature_c = Convert.ToInt32(point["temp_c"]), level = level == "8" ? "Max" : level });
                    }
                }
            }
            if (value == null) throw new ArgumentException("The JSON configuration is empty."); value.Validate();
            List<CurvePoint> selected;
            if (profile != null && value.profile_curves != null && value.profile_curves.TryGetValue(profile.Id, out selected)) value.curve = selected;
            return value;
        }

        internal void Validate()
        {
            if (schema_version != 1) throw new ArgumentException("schema_version must be 1.");
            if (poll_interval_ms < 500 || poll_interval_ms > 3000) throw new ArgumentException("poll_interval_ms must be between 500 and 3000.");
            if (hysteresis_c < 1 || hysteresis_c > 10) throw new ArgumentException("hysteresis_c must be between 1 and 10.");
            if (critical_temperature_c < 75 || critical_temperature_c > 95) throw new ArgumentException("critical_temperature_c must be between 75 and 95.");
            ValidateCurve(curve);
            if (profile_curves != null) foreach (KeyValuePair<string, List<CurvePoint>> entry in profile_curves)
            {
                if (EcProfiles.Find(entry.Key) == null) throw new ArgumentException("Unknown profile in profile_curves: " + entry.Key);
                ValidateCurve(entry.Value);
            }
        }

        private void ValidateCurve(List<CurvePoint> points)
        {
            List<CurvePoint> curve = points;
            if (curve == null || curve.Count < 2 || curve.Count > 16 || curve[0] == null || curve[0].temperature_c != 0)
                throw new ArgumentException("The curve must have 2–16 points and start at 0 °C.");
            int previousTemp = -1, previousLevel = 0;
            foreach (CurvePoint point in curve)
            {
                if (point == null || point.temperature_c <= previousTemp || point.temperature_c > critical_temperature_c)
                    throw new ArgumentException("Temperatures must increase strictly and stay within the critical threshold.");
                int level = point.NumericLevel;
                if (level < previousLevel) throw new ArgumentException("Fan levels must stay the same or increase as temperature rises.");
                previousTemp = point.temperature_c; previousLevel = level;
            }
        }

        internal int LevelAt(int temperature)
        { int level = curve[0].NumericLevel; foreach (CurvePoint point in curve) { if (temperature < point.temperature_c) break; level = point.NumericLevel; } return level; }

        internal int ThresholdFor(int level)
        { return curve.First(point => point.NumericLevel == level).temperature_c; }
    }
}

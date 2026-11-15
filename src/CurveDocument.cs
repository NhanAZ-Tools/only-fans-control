using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace OnlyFansControl
{
    // Keep the original document: selecting a profile must not replace the shared curve on save.
    internal sealed class CurveDocument
    {
        private readonly string path, original;
        private readonly EcProfile profile;
        internal readonly FanConfig Settings;
        internal readonly bool Legacy;
        internal string Scope { get { return Legacy || profile == null ? "Shared curve" : profile.Name + " curve"; } }

        internal CurveDocument(string path, EcProfile profile)
        {
            this.path = path; this.profile = profile; original = File.ReadAllText(path);
            Settings = FanConfig.Parse(original, profile);
            Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(original) as Dictionary<string, object>;
            Legacy = !root.ContainsKey("schema_version") || Convert.ToInt32(root["schema_version"]) == 0;
        }

        internal string Serialize(List<CurvePoint> points)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = (Dictionary<string, object>)serializer.DeserializeObject(original);
            if (Legacy)
            {
                Dictionary<string, object> policy = (Dictionary<string, object>)root["fan_policy"];
                policy["smart_curve"] = points.Select(p => new { temp_c = p.temperature_c, level = p.NumericLevel }).ToArray();
            }
            else if (profile == null) root["curve"] = points;
            else
            {
                object existing;
                Dictionary<string, object> overrides = root.TryGetValue("profile_curves", out existing) ? existing as Dictionary<string, object> : null;
                if (overrides == null) overrides = new Dictionary<string, object>();
                overrides[profile.Id] = points; root["profile_curves"] = overrides;
            }
            string text = serializer.Serialize(root);
            FanConfig.Parse(text, profile); // Validate every curve and all safety settings before writing.
            return text;
        }

        internal void Save(List<CurvePoint> points)
        {
            string text = Serialize(points);
            if (File.ReadAllText(path) != original)
                throw new IOException("The configuration changed while this editor was open. Close and reopen the editor to load the latest settings.");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text + Environment.NewLine, new UTF8Encoding(false));
                File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

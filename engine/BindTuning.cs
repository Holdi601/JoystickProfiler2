using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Engine.Axis
{
    // What a game (and aircraft) uses for a bind's axis: the most specific tuning the relation has, else the base the
    // bind always had (DCS parameters), fitted to the game's own model. Scopes, most specific first:
    //   plane:<game>:<plane>  game:<game>  family:<family>  (base)
    // Families are games with the same response model and files: their settings carry over between them.
    public sealed class ResolvedTuning
    {
        public AxisTuning Tuning;
        public AxisModel Model;
        // The scope the values came from ("base" when inherited from the bind's own fields).
        public string Source;
        // True when the values were fitted from the base curve (no tuning for the game itself).
        public bool Fitted;
        // Detent mode (Source "detent"): what the aircraft gets and how well the game expresses it.
        public string Note = string.Empty;
    }

    public static class BindTuning
    {
        public const string BaseScope = "base";

        private static readonly Dictionary<string, string> Families = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "IL2Game", "il2" },
            { "IL2Korea", "il2" },
            { "VTOLVR", "vtolvr" },
            { "VTOLVRNative", "vtolvr" },
            { "Arma2", "arma2" },
            { "Arma2OA", "arma2" },
            // One game per version since 11/12 and 2020/2024 were split: a tuning saved for the single game before
            // ("game:XPlane", "game:MSFS") is migrated to the family (LegacyGameIds), so both versions keep it.
            { "XPlane12", "xplane" },
            { "XPlane11", "xplane" },
            { "MSFS2024", "msfs" },
            { "MSFS2020", "msfs" }
        };

        // Families that were split: their old scope is still read, after the game's own scopes. "family:arma" held
        // Arma 3 and Arma 2/OA together while all three used the generic model (only invert ever reached the files);
        // now Arma 3 ("arma3" model, no family) and Arma 2/OA ("arma2" model, family:arma2) have their own parameters,
        // and an old family:arma tuning still gives them its invert/slider (it has none of the new parameters).
        private static readonly Dictionary<string, string> LegacyFamilies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Arma3", "arma" },
            { "Arma2", "arma" },
            { "Arma2OA", "arma" }
        };

        public static string GameScope(string game)
        {
            return "game:" + (game ?? string.Empty).Trim();
        }

        public static string PlaneScope(string game, string plane)
        {
            return "plane:" + (game ?? string.Empty).Trim() + ":" + (plane ?? string.Empty).Trim();
        }

        public static string FamilyOf(string game)
        {
            string family;
            return Families.TryGetValue(game ?? string.Empty, out family) ? family : string.Empty;
        }

        public static bool IsValidScope(string scope)
        {
            string s = (scope ?? string.Empty).Trim();
            if (s == BaseScope)
                return true;
            string[] parts = s.Split(':');
            if (parts[0] == "game" || parts[0] == "family")
                return parts.Length == 2 && parts[1].Length > 0;
            if (parts[0] == "plane")
                return parts.Length >= 3 && parts[1].Length > 0 && string.Join(":", parts.Skip(2)).Length > 0;
            return false;
        }

        public static AxisTuning Base(Bind bind)
        {
            AxisTuning tuning = new AxisTuning();
            if (bind == null)
                return tuning;
            tuning.Inverted = bind.Inverted == true;
            tuning.Slider = bind.Slider == true;
            tuning.Deadzone = bind.Deadzone;
            tuning.SaturationX = bind.SaturationX;
            tuning.SaturationY = bind.SaturationY;
            tuning.Curvature = bind.Curvature == null || bind.Curvature.Count == 0 ? new List<double> { 0.0 } : new List<double>(bind.Curvature);
            return tuning;
        }

        public static AxisTuning Get(Bind bind, string scope)
        {
            if (scope == BaseScope)
                return Base(bind);
            string text;
            return bind != null && bind.TuningOverrides != null && bind.TuningOverrides.TryGetValue(scope, out text) ? AxisTuning.Parse(text) : null;
        }

        public static void Set(Bind bind, string scope, AxisTuning tuning)
        {
            if (scope == BaseScope)
            {
                bind.Inverted = tuning.Inverted;
                bind.Slider = tuning.Slider;
                bind.Deadzone = tuning.Deadzone;
                bind.SaturationX = tuning.SaturationX;
                bind.SaturationY = tuning.SaturationY;
                bind.Curvature = new List<double>(tuning.Curvature);
                return;
            }
            if (bind.TuningOverrides == null)
                bind.TuningOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bind.TuningOverrides[scope] = tuning.Format();
        }

        public static bool Clear(Bind bind, string scope)
        {
            if (bind == null || bind.TuningOverrides == null || !bind.TuningOverrides.Remove(scope))
                return false;
            if (bind.TuningOverrides.Count == 0)
                bind.TuningOverrides = null;
            return true;
        }

        public static IEnumerable<string> Scopes(string game, string plane)
        {
            if (!string.IsNullOrWhiteSpace(plane))
                yield return PlaneScope(game, plane);
            yield return GameScope(game);
            string family = FamilyOf(game);
            if (family.Length > 0)
                yield return "family:" + family;
            string legacy;
            if (LegacyFamilies.TryGetValue(game ?? string.Empty, out legacy) && legacy != family)
                yield return "family:" + legacy;
            // The scope of the single game a version was split from (game:MSFS, game:XPlane), until the profile is
            // loaded again (the load moves it to the family).
            string legacyGame = JoyPro.Engine.Games.LegacyGameIds.LegacyOf(game);
            if (legacyGame.Length > 0)
                yield return GameScope(legacyGame);
        }

        // The model a scope's values are in (game and plane scopes: that game's; a family: its first member's).
        public static AxisModel ModelForScope(string scope)
        {
            string[] parts = (scope ?? string.Empty).Trim().Split(':');
            if (parts.Length >= 2 && (parts[0] == "game" || parts[0] == "plane"))
                return AxisModels.ForGame(parts[1]);
            if (parts.Length >= 2 && parts[0] == "family")
            {
                foreach (KeyValuePair<string, string> member in Families)
                {
                    if (string.Equals(member.Value, parts[1], StringComparison.OrdinalIgnoreCase))
                        return AxisModels.ForGame(member.Key);
                }
            }
            return null;
        }

        public const string DetentSource = "detent";

        // Scopes first (an aircraft's or game's own tuning wins), then the relation's detent mode (EngineAxisDetents:
        // the base bent so the device's detent is the aircraft's afterburner), then the base.
        public static ResolvedTuning Resolve(Bind bind, string game, string plane)
        {
            ResolvedTuning normal = ResolveWithoutDetent(bind, game, plane);
            if (normal.Source != BaseScope)
                return normal;
            ResolvedTuning detent = JoyPro.Engine.EngineAxisDetents.Resolve(bind, game, plane, normal);
            return detent ?? normal;
        }

        public static bool IsTuningScopeKey(string key)
        {
            return !string.Equals(key, DetentSettings.Key, StringComparison.OrdinalIgnoreCase);
        }

        public static ResolvedTuning ResolveWithoutDetent(Bind bind, string game, string plane)
        {
            AxisModel model = AxisModels.ForGame(game);
            foreach (string scope in Scopes(game, plane))
            {
                AxisTuning tuning = Get(bind, scope);
                if (tuning != null)
                    return new ResolvedTuning { Tuning = tuning, Model = model, Source = scope };
            }
            AxisTuning baseTuning = Base(bind);
            if (model.Id == AxisModels.Dcs || model.Fit == null)
                return new ResolvedTuning { Tuning = baseTuning, Model = model, Source = BaseScope };
            return new ResolvedTuning { Tuning = AxisModels.Inherit(baseTuning, model), Model = model, Source = BaseScope, Fitted = true };
        }
    }
}

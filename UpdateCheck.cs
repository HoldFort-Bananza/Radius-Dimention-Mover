using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RadiusDimensionMover
{
    /// <summary>
    /// Sprawdza przy starcie, czy na GitHubie jest nowsza wersja programu.
    ///
    /// Zasady, które tu obowiązują:
    /// - **Cisza, gdy wszystko aktualne.** Powiadomienie pojawia się TYLKO gdy
    ///   dostępna wersja jest wyższa. Brak internetu, błąd API, nieznana
    ///   odpowiedź - też cisza. Program ma działać bez internetu.
    /// - **Nigdy nie blokuje startu.** Cała robota idzie w tle
    ///   (Task.Run + krótkie timeouty); UI nie czeka na sieć.
    /// - **Nigdy nie wywala programu.** Każdy wyjątek jest łapany i tłumiony -
    ///   to funkcja pomocnicza, nie krytyczna.
    ///
    /// Wersja własna czytana jest z assembly (`Version` w .csproj), dostępna z
    /// tagu release (`v1.2` → `1.2`). Te dwie wartości plus `MyAppVersion` w
    /// `installer/setup.iss` muszą być zgodne.
    /// </summary>
    internal static class UpdateCheck
    {
        private const string LatestReleaseApi =
            "https://api.github.com/repos/HoldFort-Bananza/Radius-Dimention-Mover/releases/latest";

        public const string ReleasesPage =
            "https://github.com/HoldFort-Bananza/Radius-Dimention-Mover/releases";

        private const int TimeoutMs = 5000;

        /// <summary>
        /// Odpala sprawdzanie w tle. `onNewerVersion` jest wołane TYLKO gdy
        /// znaleziono nowszą wersję - dostaje jej numer (np. "1.3"). Wołane z
        /// wątku roboczego, więc odbiorca musi sam wrócić na wątek UI.
        ///
        /// `log` służy tylko do diagnostyki - wynik "jesteś aktualny" też tam
        /// trafia, żeby po logu było widać, że sprawdzenie się odbyło.
        /// </summary>
        public static void StartInBackground(Action<string> onNewerVersion, Action<string> log)
        {
            Task.Run(() =>
            {
                try
                {
                    Version installed = GetInstalledVersion();
                    if (installed == null)
                    {
                        return;   // nie znamy własnej wersji - nie ma co porównywać
                    }

                    string tag = FetchLatestTag();
                    if (string.IsNullOrEmpty(tag))
                    {
                        return;   // brak internetu albo nieoczekiwana odpowiedź
                    }

                    Version latest = ParseVersion(tag);
                    if (latest == null)
                    {
                        return;
                    }

                    if (latest > installed)
                    {
                        log?.Invoke("Dostępna nowsza wersja: " + Format(latest)
                            + " (masz " + Format(installed) + ").");
                        onNewerVersion?.Invoke(Format(latest));
                    }
                    else
                    {
                        log?.Invoke("Wersja " + Format(installed) + " jest aktualna.");
                    }
                }
                catch
                {
                    // Świadomie cicho - sprawdzanie aktualizacji nie może
                    // zepsuć uruchomienia programu.
                }
            });
        }

        /// <summary>
        /// Wersja z własnego assembly, znormalizowana do trzech składowych.
        /// </summary>
        private static Version GetInstalledVersion()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? null : Normalize(v);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Pobiera `tag_name` najnowszego release. Zwraca null przy
        /// jakimkolwiek problemie (brak sieci, timeout, błąd HTTP).
        ///
        /// GitHub API wymaga nagłówka User-Agent - bez niego odpowiada 403.
        /// Odpowiedź parsowana jest wyrażeniem regularnym, żeby nie wciągać
        /// zależności na JSON dla jednego pola.
        /// </summary>
        private static string FetchLatestTag()
        {
            try
            {
                // Na .NET Framework domyślny protokół bywa za stary dla
                // api.github.com - wymuszamy TLS 1.2.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                var request = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
                request.UserAgent = "RadiusDimensionMover";
                request.Accept = "application/vnd.github+json";
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return null;
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        string json = reader.ReadToEnd();
                        var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                        return m.Success ? m.Groups[1].Value : null;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Zamienia tag release na wersję. Wiodące "v" jest opcjonalne, więc
        /// zarówno "v1.2" jak i "1.2" są rozumiane. Tagi nienumeryczne (np.
        /// dawny tag "Main") dają null i są ignorowane.
        /// </summary>
        private static Version ParseVersion(string tag)
        {
            string cleaned = tag.Trim().TrimStart('v', 'V');

            Version parsed;
            if (!Version.TryParse(cleaned, out parsed))
            {
                return null;
            }

            return Normalize(parsed);
        }

        /// <summary>
        /// Ujednolica liczbę składowych. Bez tego Version("1.2") jest MNIEJSZE
        /// niż Version("1.2.0"), bo brakujące składowe mają wartość -1 - i
        /// program zgłaszałby aktualizację przy identycznej wersji.
        /// </summary>
        private static Version Normalize(Version v)
        {
            return new Version(
                v.Major,
                v.Minor < 0 ? 0 : v.Minor,
                v.Build < 0 ? 0 : v.Build);
        }

        private static string Format(Version v)
        {
            return v.Build > 0
                ? v.Major + "." + v.Minor + "." + v.Build
                : v.Major + "." + v.Minor;
        }
    }
}

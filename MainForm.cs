using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Tekla.Structures.Drawing;

namespace RadiusDimensionMover
{
    public class MainForm : Form
    {
        private readonly RadiusDimensionService _service = new RadiusDimensionService();

        // Przycisk "Przesuń" jest ZAWSZE klikalny - żadnej blokady. Wcześniej
        // blokował się po przesunięciu i odblokowywał tylko, gdy program sam
        // wykrył zmianę, ale Tekla nie zgłasza cofnięcia przez Ctrl+Z, więc po
        // Ctrl+Z przycisk zostawał szary i nie było jak przesunąć ponownie.
        // Przesunięcie drugi raz nic nie psuje (wymiar po prostu zostaje
        // rozstawiony od nowa), więc blokada przynosiła więcej szkody niż
        // pożytku.

        // Ustawiane na czas trwania przesuwania - tylko po to, żeby nie
        // wystartować drugiej operacji w trakcie pierwszej.
        private bool _busy;

        // Wykrywanie zmiany kontekstu w Tekli ZDARZENIAMI, nie pingowaniem -
        // tak samo jak w HFT_Organizer_Mostowy (UI/MainForm.cs,
        // RegisterTeklaEvents): Tekla.Structures.Drawing.UI.Events daje
        // DrawingLoaded (wejście na inny rysunek), DrawingEditorOpened/Closed
        // (wejście/wyjście z edytora rysunków), a Model.Events dodatkowo
        // TeklaStructuresExit. Dzięki temu przycisk reaguje od razu, bez
        // odpytywania Tekli w kółko.
        private Tekla.Structures.Drawing.UI.Events _drawingEvents;
        private Tekla.Structures.Model.Events _modelEvents;

        // Timer działa TYLKO dopóki nie ma połączenia z Teklą (wtedy nie ma
        // do czego przyczepić zdarzeń) - po połączeniu jest zatrzymywany, żeby
        // nie pingować Tekli bez potrzeby.
        private Timer _connectRetryTimer;
        private const int ConnectRetryIntervalMs = 3000;

        // Ścieżka do pliku logu tej sesji - zapisywana automatycznie, żeby
        // nie trzeba było ręcznie kopiować zawartości okna logu przy
        // zgłaszaniu problemu. Jeden plik na uruchomienie programu, w
        // podfolderze "logs" obok pliku .exe.
        private readonly string _logFilePath;

        private Button _runButton;
        private Button _undoButton;
        private TextBox _logBox;
        private Label _statusLabel;

        public MainForm()
        {
            _logFilePath = InitLogFile();

            Text = "Radius Dimension Mover – Tekla 2025";
            Width = 520;
            Height = 460;
            StartPosition = FormStartPosition.CenterScreen;

            // Jeden przycisk, bez żadnych parametrów. Cofania nie ma - w Tekli
            // działa zwykłe Ctrl+Z, więc program tylko pilnuje, żeby nie
            // przesunąć dwa razy pod rząd tego samego rysunku.
            _runButton = new Button
            {
                Text = "Przesuń wszystkie wymiary R (unikaj kolizji)",
                Left = 15,
                Top = 15,
                Width = 470,
                Height = 40
            };
            _runButton.Click += RunButton_Click;

            _statusLabel = new Label
            {
                Left = 15,
                Top = 64,
                Width = 470,
                Height = 20,
                ForeColor = Color.DarkSlateGray
            };

            _logBox = new TextBox
            {
                Left = 15,
                Top = 89,
                Width = 470,
                Height = 298,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Font = new Font("Consolas", 9)
            };

            Controls.Add(_runButton);
            Controls.Add(_statusLabel);
            Controls.Add(_logBox);

            // Fokus okna to dodatkowy, tani moment na odświeżenie - łapie też
            // zmiany, których zdarzenia Tekli nie zgłaszają (np. ręczne
            // przeciągnięcie wymiaru na rysunku).
            Activated += (s, e) => RefreshState();

            _connectRetryTimer = new Timer { Interval = ConnectRetryIntervalMs };
            _connectRetryTimer.Tick += (s, e) => TryConnectAndWatch();

            FormClosing += (s, e) =>
            {
                try { _connectRetryTimer?.Stop(); } catch { }
                UnregisterTeklaEvents();
            };

            Log($"===== Start sesji {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            Log(_logFilePath != null
                ? "Log tej sesji zapisywany do pliku: " + _logFilePath
                : "UWAGA: nie udało się utworzyć pliku logu - log dostępny tylko w tym oknie.");

            TryConnectAndWatch();
        }

        /// <summary>
        /// Podłącza się do zdarzeń Tekli, jeśli tylko jest połączenie. Dopóki
        /// Tekli nie ma (albo się zamknęła), chodzi timer ponawiający próbę -
        /// po udanej rejestracji timer jest zatrzymywany, żeby nie pingować
        /// Tekli bez potrzeby (ten sam wzorzec co w HFT_Organizer_Mostowy).
        /// </summary>
        private void TryConnectAndWatch()
        {
            bool connected;
            try
            {
                connected = new DrawingHandler().GetConnectionStatus();
            }
            catch
            {
                connected = false;
            }

            if (connected)
            {
                if (_drawingEvents == null)
                {
                    RegisterTeklaEvents();
                }
                _connectRetryTimer?.Stop();
            }
            else
            {
                // Rejestracje wskazywałyby na nieistniejący już proces Tekli -
                // trzeba je porzucić, inaczej po ponownym starcie Tekli
                // program nigdy nie dostanie już żadnego zdarzenia.
                UnregisterTeklaEvents();
                if (_connectRetryTimer != null && !_connectRetryTimer.Enabled)
                {
                    _connectRetryTimer.Start();
                }
            }

            RefreshState();
        }

        private void RegisterTeklaEvents()
        {
            try
            {
                _drawingEvents = new Tekla.Structures.Drawing.UI.Events();
                _drawingEvents.DrawingLoaded += OnTeklaContextChanged;
                _drawingEvents.DrawingEditorOpened += OnTeklaContextChanged;
                _drawingEvents.DrawingEditorClosed += OnTeklaContextChanged;
                _drawingEvents.Register();

                _modelEvents = new Tekla.Structures.Model.Events();
                _modelEvents.TeklaStructuresExit += OnTeklaExited;
                _modelEvents.Register();

                Log("Wykrywanie zmiany rysunku: zdarzeniowe (DrawingLoaded / DrawingEditorOpened / DrawingEditorClosed).");
            }
            catch (Exception ex)
            {
                // Bez zdarzeń program nadal działa - po prostu stan odświeży
                // się przy powrocie fokusu na okno.
                Log("UWAGA: zdarzenia Tekli niedostępne (" + ex.Message
                    + ") - stan przycisków odświeży się przy kliknięciu w to okno.");
                _drawingEvents = null;
                _modelEvents = null;
            }
        }

        private void UnregisterTeklaEvents()
        {
            try { if (_drawingEvents != null) { _drawingEvents.UnRegister(); } } catch { }
            try { if (_modelEvents != null) { _modelEvents.UnRegister(); } } catch { }
            _drawingEvents = null;
            _modelEvents = null;
        }

        // Zdarzenia Tekli przychodzą z jej wątku - do UI musimy wrócić przez
        // Invoke, inaczej ruszanie przyciskami rzuci wyjątkiem.
        private void OnTeklaContextChanged() => UiInvoke(RefreshState);

        private void OnTeklaExited() => UiInvoke(() =>
        {
            UnregisterTeklaEvents();
            RefreshState();
            _connectRetryTimer?.Start();
        });

        private void UiInvoke(Action action)
        {
            try
            {
                if (IsDisposed || Disposing)
                {
                    return;
                }

                if (InvokeRequired)
                {
                    BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch
            {
                // Okno mogło właśnie zniknąć - nic tu nie poradzimy i nie ma
                // sensu przerywać z tego powodu działania programu.
            }
        }

        /// <summary>
        /// Odświeża tylko PODPIS pod przyciskiem (jaki rysunek jest teraz
        /// otwarty). Sam przycisk pozostaje zawsze klikalny. Wołane ze zdarzeń
        /// Tekli - musi być tanie i nigdy nie może rzucić wyjątkiem w górę.
        /// </summary>
        private void RefreshState()
        {
            if (_busy)
            {
                return;
            }

            try
            {
                _statusLabel.Text = _service.GetCurrentDrawingDescription();
            }
            catch (Exception ex)
            {
                // Nie da się odczytać stanu (np. Tekla właśnie się zamyka) -
                // to tylko podpis, nie ma sensu robić z tego błędu.
                _statusLabel.Text = "Brak kontaktu z Teklą (" + ex.GetType().Name + ").";
            }
        }

        private void RunButton_Click(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            _logBox.Clear();
            Log($"===== {DateTime.Now:HH:mm:ss} PRZESUŃ (auto, unikanie kolizji) =====");
            _busy = true;
            _runButton.Enabled = false;
            _statusLabel.Text = "Przetwarzanie (może chwilę potrwać - Tekla liczy rozstawienie)...";

            try
            {
                var result = _service.AutoPlaceWithCollisionAvoidance(Log);
                _statusLabel.Text = $"Gotowe. Rozstawiono {result.MovedCount} z {result.TotalCount} wymiarów R. Sprawdź wizualnie w Tekli.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Błąd – zobacz log.";
                Log("BŁĄD: " + ex.Message);
                Log(ex.StackTrace);
            }
            finally
            {
                _busy = false;
                _runButton.Enabled = true;
            }
        }

        /// <summary>
        /// Tworzy podfolder "logs" obok pliku .exe i zwraca ścieżkę do
        /// nowego pliku logu na tę sesję (jedno uruchomienie programu =
        /// jeden plik, wszystkie akcje dopisywane po kolei). Jeśli z
        /// jakiegoś powodu nie da się utworzyć folderu/pliku (np. brak
        /// uprawnień), program ma dalej działać - po prostu bez logu do pliku.
        /// </summary>
        private static string InitLogFile()
        {
            try
            {
                string logDir = Path.Combine(Application.StartupPath, "logs");
                Directory.CreateDirectory(logDir);
                return Path.Combine(logDir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            }
            catch
            {
                return null;
            }
        }

        private void Log(string message)
        {
            _logBox.AppendText(message + Environment.NewLine);

            if (_logFilePath == null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_logFilePath, message + Environment.NewLine);
            }
            catch
            {
                // Błąd zapisu do pliku loga nie może przerwać działania
                // programu - to tylko dodatkowa wygoda, nie krytyczna funkcja.
            }
        }
    }
}

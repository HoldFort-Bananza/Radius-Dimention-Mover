using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RadiusDimensionMover
{
    public class MainForm : Form
    {
        // WAŻNE: jeden trwały obiekt serwisu przez cały czas życia okna,
        // żeby historia do "Cofnij" przetrwała między kliknięciami przycisków.
        private readonly RadiusDimensionService _service = new RadiusDimensionService();

        // Blokada przycisku "Przesuń" po udanym przesunięciu - dopóki nie
        // klikniesz "Cofnij", kolejne kliknięcia "Przesuń" nic nie robią.
        // Chroni to przed sytuacją, gdy Tekla "zawiesza się" na chwilę,
        // użytkownik klika kilka razy myśląc że nic się nie stało.
        private bool _canRun = true;

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

            // --- Wiersz 1: Przesuń - jeden przycisk, bez żadnych parametrów.
            // Tekla sama szuka wolnego miejsca (Placing=Free, patrz
            // RadiusDimensionService.AutoPlaceWithCollisionAvoidance). ---
            _runButton = new Button
            {
                Text = "Przesuń wszystkie wymiary R (unikaj kolizji)",
                Left = 15,
                Top = 15,
                Width = 470,
                Height = 40
            };
            _runButton.Click += RunButton_Click;

            // --- Wiersz 2: Cofnij ---
            _undoButton = new Button
            {
                Text = "Cofnij",
                Left = 15,
                Top = 62,
                Width = 470,
                Height = 32
            };
            _undoButton.Click += UndoButton_Click;

            _statusLabel = new Label
            {
                Left = 15,
                Top = 102,
                Width = 470,
                Height = 20,
                ForeColor = Color.DarkSlateGray
            };

            _logBox = new TextBox
            {
                Left = 15,
                Top = 127,
                Width = 470,
                Height = 260,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Font = new Font("Consolas", 9)
            };

            Controls.Add(_runButton);
            Controls.Add(_undoButton);
            Controls.Add(_statusLabel);
            Controls.Add(_logBox);

            // Naturalny moment, żeby sprawdzić, czy ktoś ręcznie poprawił
            // wymiar w Tekli albo otworzył inny rysunek: użytkownik musi
            // kliknąć z powrotem na to okno, żeby móc znów użyć "Przesuń" -
            // to właśnie odpala Activated.
            Activated += MainForm_Activated;

            Log($"===== Start sesji {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            Log(_logFilePath != null
                ? "Log tej sesji zapisywany do pliku: " + _logFilePath
                : "UWAGA: nie udało się utworzyć pliku logu - log dostępny tylko w tym oknie.");
        }

        private void MainForm_Activated(object sender, EventArgs e)
        {
            if (_canRun)
            {
                // Już odblokowane - nie ma sensu odpytywać Tekli.
                return;
            }

            try
            {
                if (_service.HasAnyDimensionChangedSinceLastMove())
                {
                    _canRun = true;
                    _runButton.Enabled = true;
                    _statusLabel.Text = "Wykryto zmianę rysunku lub ręczną zmianę wymiaru – przycisk Przesuń odblokowany.";
                }
            }
            catch
            {
                // Ciche pominięcie - nie chcemy wyskakujących błędów przy
                // zwykłym przełączeniu się z powrotem na to okno.
            }
        }

        private void RunButton_Click(object sender, EventArgs e)
        {
            if (!_canRun)
            {
                // Zabezpieczenie na wypadek, gdyby kliknięcie mimo wszystko
                // dotarło (np. kolejka komunikatów) mimo że przycisk powinien
                // być zablokowany - nic nie rób, nie przesuwaj drugi raz.
                return;
            }

            _logBox.Clear();
            Log($"===== {DateTime.Now:HH:mm:ss} PRZESUŃ (auto, unikanie kolizji) =====");
            SetButtonsEnabled(false);
            _statusLabel.Text = "Przetwarzanie (może chwilę potrwać - Tekla liczy rozstawienie)...";

            try
            {
                var result = _service.AutoPlaceWithCollisionAvoidance(Log);

                // Po udanym przesunięciu blokujemy "Przesuń", żeby kolejne
                // kliknięcia (np. z niecierpliwości) nie powtarzały operacji
                // niepotrzebnie. Żeby przesunąć dalej, trzeba świadomie
                // kliknąć "Cofnij" i spróbować ponownie.
                _canRun = false;

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
                SetButtonsEnabled(true);
            }
        }

        private void UndoButton_Click(object sender, EventArgs e)
        {
            _logBox.Clear();
            Log($"===== {DateTime.Now:HH:mm:ss} COFNIJ =====");
            SetButtonsEnabled(false);
            _statusLabel.Text = "Cofanie...";

            try
            {
                var result = _service.UndoLastMove(Log);

                if (result.TotalCount == 0)
                {
                    _statusLabel.Text = "Brak historii do cofnięcia.";
                }
                else
                {
                    _statusLabel.Text = $"Cofnięto {result.MovedCount} z {result.TotalCount} wymiarów R.";
                }

                // Cofnięcie (nawet gdy nie było czego cofać) odblokowuje
                // "Przesuń" - wracamy do stanu, w którym jedno kliknięcie
                // = jedno przesunięcie.
                _canRun = true;
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Błąd – zobacz log.";
                Log("BŁĄD: " + ex.Message);
                Log(ex.StackTrace);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            // "Przesuń" wraca do stanu aktywnego tylko jeśli nie jest
            // zablokowany przez _canRun (czyli dopóki nie kliknięto "Cofnij"
            // po ostatnim udanym przesunięciu).
            _runButton.Enabled = enabled && _canRun;
            _undoButton.Enabled = enabled;
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

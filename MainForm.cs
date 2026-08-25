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

        // Kierunek jest jawnym stanem, zmienianym WYŁĄCZNIE przez kliknięcie
        // przycisku "Odwróć kierunek". Kliknięcie tego przycisku NIE przesuwa
        // żadnych wymiarów - tylko zmienia flagę, użytą przy następnym
        // kliknięciu "Przesuń".
        private bool _oppositeDirection = false;

        // Blokada przycisku "Przesuń" po udanym przesunięciu - dopóki nie
        // klikniesz "Cofnij", kolejne kliknięcia "Przesuń" nic nie robią.
        // Chroni to przed sytuacją, gdy Tekla "zawiesza się" na chwilę,
        // użytkownik klika kilka razy myśląc że nic się nie stało, i wymiar
        // wylatuje daleko poza rysunek zamiast przesunąć się raz o krok.
        private bool _canRun = true;

        // Ścieżka do pliku logu tej sesji - zapisywana automatycznie, żeby
        // nie trzeba było ręcznie kopiować zawartości okna logu przy
        // zgłaszaniu problemu. Jeden plik na uruchomienie programu, w
        // podfolderze "logs" obok pliku .exe.
        private readonly string _logFilePath;

        private NumericUpDown _offsetInput;
        private Button _toggleDirectionButton;
        private Button _runButton;
        private Button _undoButton;
        private Label _directionLabel;
        private TextBox _logBox;
        private Label _statusLabel;

        public MainForm()
        {
            _logFilePath = InitLogFile();

            Text = "Radius Dimension Mover – Tekla 2025";
            Width = 520;
            Height = 500;
            StartPosition = FormStartPosition.CenterScreen;

            // --- Wiersz 1: aktualny kierunek + przycisk odwracający ---
            _directionLabel = new Label
            {
                Left = 15,
                Top = 18,
                Width = 300,
                Height = 20,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            UpdateDirectionLabel();

            _toggleDirectionButton = new Button
            {
                Text = "Odwróć kierunek",
                Left = 330,
                Top = 12,
                Width = 155,
                Height = 32
            };
            _toggleDirectionButton.Click += ToggleDirectionButton_Click;

            // --- Wiersz 2: krok przesunięcia + przycisk Przesuń (razem, obok siebie) ---
            var offsetLabel = new Label
            {
                Text = "Krok [mm]:",
                Left = 15,
                Top = 62,
                Width = 75
            };

            _offsetInput = new NumericUpDown
            {
                Left = 95,
                Top = 58,
                Width = 80,
                Minimum = 1,
                Maximum = 5000,
                // Krok jest w mm NA PAPIERZE (przeliczane przez skalę widoku
                // w RadiusDimensionService) - niezależnie od skali rysunku
                // powinien dawać przewidywalny, widoczny ruch.
                Value = 40
            };

            _runButton = new Button
            {
                Text = "Przesuń wszystkie wymiary R (+krok)",
                Left = 185,
                Top = 56,
                Width = 300,
                Height = 35
            };
            _runButton.Click += RunButton_Click;

            // --- Wiersz 3: Cofnij ---
            _undoButton = new Button
            {
                Text = "Cofnij",
                Left = 15,
                Top = 100,
                Width = 470,
                Height = 32
            };
            _undoButton.Click += UndoButton_Click;

            _statusLabel = new Label
            {
                Left = 15,
                Top = 142,
                Width = 470,
                Height = 20,
                ForeColor = Color.DarkSlateGray
            };

            _logBox = new TextBox
            {
                Left = 15,
                Top = 167,
                Width = 470,
                Height = 260,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Font = new Font("Consolas", 9)
            };

            Controls.Add(_directionLabel);
            Controls.Add(_toggleDirectionButton);
            Controls.Add(offsetLabel);
            Controls.Add(_offsetInput);
            Controls.Add(_runButton);
            Controls.Add(_undoButton);
            Controls.Add(_statusLabel);
            Controls.Add(_logBox);

            // Naturalny moment, żeby sprawdzić, czy ktoś ręcznie poprawił
            // wymiar w Tekli: użytkownik musi kliknąć z powrotem na to okno,
            // żeby móc znów użyć "Przesuń" - to właśnie odpala Activated.
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
                    _statusLabel.Text = "Wykryto ręczną zmianę wymiaru na rysunku – przycisk Przesuń odblokowany.";
                }
            }
            catch
            {
                // Ciche pominięcie - nie chcemy wyskakujących błędów przy
                // zwykłym przełączeniu się z powrotem na to okno.
            }
        }

        private void ToggleDirectionButton_Click(object sender, EventArgs e)
        {
            // Samo kliknięcie TYLKO zmienia stan - żadnego wywołania do Tekli,
            // żadnego przesunięcia. Efekt będzie widoczny dopiero po
            // kolejnym kliknięciu "Przesuń".
            _oppositeDirection = !_oppositeDirection;
            UpdateDirectionLabel();
        }

        private void UpdateDirectionLabel()
        {
            _directionLabel.Text = _oppositeDirection
                ? "Kierunek: PRZECIWNY (przez środek)"
                : "Kierunek: normalny (zgodnie ze strzałką)";
            _directionLabel.ForeColor = _oppositeDirection ? Color.DarkOrange : Color.DarkSlateGray;
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
            double offsetMm = (double)_offsetInput.Value;
            Log($"===== {DateTime.Now:HH:mm:ss} PRZESUŃ - krok: {offsetMm}mm, kierunek: {(_oppositeDirection ? "przeciwny" : "normalny")} =====");
            SetButtonsEnabled(false);
            _statusLabel.Text = "Przetwarzanie...";

            try
            {
                var result = _service.MoveAllRadiusDimensionsOutward(offsetMm, _oppositeDirection, Log);

                // Po udanym przesunięciu blokujemy "Przesuń", żeby kolejne
                // kliknięcia (np. z niecierpliwości, gdy Tekla chwilę nie
                // odpowiada) nie dokładały kroku jeszcze raz. Żeby przesunąć
                // dalej, trzeba świadomie kliknąć "Cofnij" i spróbować ponownie.
                _canRun = false;

                _statusLabel.Text = $"Gotowe. Przesunięto {result.MovedCount} z {result.TotalCount} wymiarów R. Sprawdź wizualnie w Tekli - jeśli trzeba dalej, kliknij Cofnij i spróbuj ponownie z innym krokiem.";
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
            _toggleDirectionButton.Enabled = enabled;
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

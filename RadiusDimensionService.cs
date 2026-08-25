using System;
using System.Collections.Generic;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;

namespace RadiusDimensionMover
{
    public class MoveResult
    {
        public int TotalCount;
        public int MovedCount;
    }

    /// <summary>
    /// Jedna zaplanowana zmiana Distance dla jednego wymiaru R - policzona,
    /// ale JESZCZE NIE zapisana do rysunku. Dzieli AutoPlace na "policz"
    /// (PlanAutoPlace, nic nie zmienia) i "zastosuj" (ApplyAutoPlace, faktycznie
    /// pisze do Tekli), żeby UI mogło pokazać ostrzeżenie PRZED zapisem, jeśli
    /// dla któregoś wymiaru nie znaleziono w pełni wolnego miejsca.
    /// </summary>
    public class AutoPlaceEntry
    {
        public RadiusDimension Dim;
        public double PreviousDistance;
        public double NewDistance;
        public double ChosenDistancePaperMm;
        public bool FoundClear;
    }

    public class AutoPlacePlan
    {
        public List<AutoPlaceEntry> Entries = new List<AutoPlaceEntry>();
        public int TotalCount;

        public bool AllClear
        {
            get
            {
                foreach (var e in Entries)
                {
                    if (!e.FoundClear)
                    {
                        return false;
                    }
                }
                return true;
            }
        }
    }

    public class RadiusDimensionService
    {
        // Stos kolejnych "przesunięć" - każde kliknięcie "Przesuń" dokłada
        // na wierzch jeden zestaw (wymiar R -> Distance SPRZED tego kliknięcia).
        // Każde kliknięcie "Cofnij" zdejmuje jeden zestaw ze stosu i przywraca
        // te wartości, więc klikając "Cofnij" wielokrotnie, wracasz krok po
        // kroku aż do stanu sprzed pierwszego "Przesuń" (oryginału).
        private readonly Stack<List<(RadiusDimension dim, double previousDistance)>> _undoStack
            = new Stack<List<(RadiusDimension, double)>>();

        // Wartości Distance ustawione przez OSTATNIE udane "Przesuń" - używane
        // do wykrycia, czy użytkownik ręcznie przesunął któryś wymiar R na
        // rysunku (np. przeciągając go w Tekli) od tego momentu. Jeśli tak,
        // UI może z powrotem odblokować przycisk "Przesuń".
        private List<(RadiusDimension dim, double appliedDistance)> _lastAppliedMove
            = new List<(RadiusDimension, double)>();

        // --- Parametry heurystyki auto-rozstawiania (do ręcznego dostrojenia,
        // jeśli w praktyce okaże się za ciasno/za luźno) ---

        // Minimalna dopuszczalna odległość [mm na papierze] między szacowanym
        // punktem tekstu R a punktem wstawienia dowolnego innego tekstu na
        // arkuszu, zanim uznamy to za kolizję.
        private const double MinClearanceMm = 30.0;

        // Od jakiej odległości od łuku [mm na papierze] zaczynamy próbować.
        private const double StartDistanceMm = 15.0;

        // O ile zwiększamy dystans w każdej kolejnej próbie [mm na papierze].
        private const double StepMm = 5.0;

        // Górny limit prób [mm na papierze] - żeby w skrajnym przypadku
        // (np. rysunek zapchany tekstami) nie uciekło w nieskończoność.
        private const double MaxDistanceMm = 300.0;

        /// <summary>
        /// KROK 1/2: liczy, gdzie powinien wylądować każdy wymiar R na
        /// aktywnym rysunku, żeby (w przybliżeniu) nie kolidować z żadnym
        /// innym tekstem na arkuszu ani z innym wymiarem R - ale NICZEGO
        /// jeszcze nie zapisuje do rysunku. UI decyduje, czy od razu wywołać
        /// ApplyAutoPlace, czy najpierw ostrzec użytkownika (gdy
        /// !plan.AllClear - nie dla każdego wymiaru znaleziono wolne miejsce).
        ///
        /// WAŻNE OGRANICZENIA:
        /// 1. Tekla Open API nie udostępnia wprost pozycji tekstu wymiaru R
        ///    (RadiusDimension nie ma bounding boxa ani punktu wstawienia).
        ///    Pozycję tekstu SZACUJEMY jako punkt ArcPoint2 przesunięty w
        ///    kierunku "od środka widoku na zewnątrz" - patrz GetOutwardDirection.
        ///    To przybliżenie, nie odczyt z API.
        /// 2. Kolizje sprawdzamy względem punktów wstawienia obiektów Text
        ///    na arkuszu oraz względem szacowanych pozycji INNYCH wymiarów R
        ///    w tym samym przebiegu. Nie sprawdzamy kolizji z liniami
        ///    konturu, strzałkami itd. - one nie mają bounding boxa w API.
        /// 3. RadiusDimension.Distance jest w jednostkach MODELU, nie papieru
        ///    - przeliczane przez skalę widoku (GetViewScale), żeby "mm" tu
        ///    zawsze znaczyło mm na papierze.
        /// </summary>
        public AutoPlacePlan PlanAutoPlace(bool oppositeDirection, Action<string> log)
        {
            var plan = new AutoPlacePlan();

            var drawingHandler = new DrawingHandler();
            if (!drawingHandler.GetConnectionStatus())
            {
                throw new InvalidOperationException(
                    "Nie można połączyć się z Tekla Structures. Upewnij się, że Tekla jest uruchomiona.");
            }

            Drawing activeDrawing = drawingHandler.GetActiveDrawing();
            if (activeDrawing == null)
            {
                throw new InvalidOperationException(
                    "Brak aktywnego rysunku. Otwórz rysunek pojedynczej części w edytorze rysunków Tekli.");
            }

            log("Aktywny rysunek: " + activeDrawing.Name);

            var sheet = activeDrawing.GetSheet();
            if (sheet == null)
            {
                throw new InvalidOperationException("Nie udało się pobrać arkusza rysunku (GetSheet).");
            }

            var radiusDimensions = new List<RadiusDimension>();
            var otherTextPoints = new List<Point>();

            DrawingObjectEnumerator objectEnum = sheet.GetAllObjects();
            while (objectEnum.MoveNext())
            {
                var current = objectEnum.Current;

                if (current is RadiusDimension rd)
                {
                    radiusDimensions.Add(rd);
                    continue;
                }

                if (current is Text txt)
                {
                    try
                    {
                        // Punkt odniesienia tekstu = środek jego bounding boxa.
                        // Text ma udokumentowaną metodę GetAxisAlignedBoundingBox(),
                        // w przeciwieństwie do RadiusDimension.
                        var box = txt.GetAxisAlignedBoundingBox();
                        var center = new Point(
                            (box.MinPoint.X + box.MaxPoint.X) / 2.0,
                            (box.MinPoint.Y + box.MaxPoint.Y) / 2.0,
                            (box.MinPoint.Z + box.MaxPoint.Z) / 2.0);
                        otherTextPoints.Add(center);
                    }
                    catch (Exception ex)
                    {
                        log("  Pominięto jeden Text przy zbieraniu punktów odniesienia - błąd: " + ex.Message);
                    }
                }
            }

            plan.TotalCount = radiusDimensions.Count;
            log("Znaleziono " + plan.TotalCount + " wymiar(ów) R oraz " + otherTextPoints.Count + " innych tekst(ów) na arkuszu.");

            // Widok/skala/środek widoku są takie same dla wymiarów z tego
            // samego widoku - liczymy raz na widok, nie raz na wymiar.
            var viewCache = new Dictionary<ViewBase, (double scale, Point center)>();

            foreach (var rd in radiusDimensions)
            {
                try
                {
                    double previousDistance = rd.Distance;

                    var (scale, viewCenter) = GetViewScaleAndCenter(rd, viewCache, log);
                    var (dirX, dirY, referencePoint) = GetOutwardDirection(rd, viewCenter);

                    double chosenDistance = StartDistanceMm;
                    bool foundClear = false;
                    double finalX = referencePoint.X, finalY = referencePoint.Y;

                    for (double d = StartDistanceMm; d <= MaxDistanceMm; d += StepMm)
                    {
                        double signedD = oppositeDirection ? -d : d;

                        double candidateX = referencePoint.X + dirX * signedD;
                        double candidateY = referencePoint.Y + dirY * signedD;

                        bool collides = false;
                        foreach (var tp in otherTextPoints)
                        {
                            if (DistanceBetween(candidateX, candidateY, tp.X, tp.Y) < MinClearanceMm)
                            {
                                collides = true;
                                break;
                            }
                        }

                        chosenDistance = d;
                        finalX = candidateX;
                        finalY = candidateY;
                        if (!collides)
                        {
                            foundClear = true;
                            break;
                        }
                    }

                    // Kolejne wymiary R w tym samym przebiegu mają też omijać
                    // miejsce, które właśnie "zajął" ten - inaczej dwa sąsiednie
                    // wymiary R mogłyby wylądować na sobie.
                    otherTextPoints.Add(new Point(finalX, finalY, referencePoint.Z));

                    double newDistance = (oppositeDirection ? -chosenDistance : chosenDistance) / scale;

                    log($"  Wymiar R: {previousDistance:F1} -> {newDistance:F1} (dystans {chosenDistance:F1}mm na papierze, skala widoku {scale:F3}{(foundClear ? ", wolne miejsce" : ", BRAK wolnego miejsca w limicie")}). [DIAG] kierunek=({dirX:F3},{dirY:F3}) szacowana pozycja=({finalX:F1},{finalY:F1})");

                    plan.Entries.Add(new AutoPlaceEntry
                    {
                        Dim = rd,
                        PreviousDistance = previousDistance,
                        NewDistance = newDistance,
                        ChosenDistancePaperMm = chosenDistance,
                        FoundClear = foundClear
                    });
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden wymiar R przy planowaniu – błąd: " + ex.Message);
                }
            }

            return plan;
        }

        /// <summary>
        /// KROK 2/2: zapisuje do rysunku plan policzony przez PlanAutoPlace.
        /// Wywołuj dopiero po ewentualnym potwierdzeniu przez użytkownika
        /// (gdy plan.AllClear == false).
        /// </summary>
        public MoveResult ApplyAutoPlace(AutoPlacePlan plan, Action<string> log)
        {
            var thisMoveHistory = new List<(RadiusDimension, double)>();
            var appliedNow = new List<(RadiusDimension, double)>();

            var result = new MoveResult { TotalCount = plan.TotalCount };

            var drawingHandler = new DrawingHandler();
            if (!drawingHandler.GetConnectionStatus())
            {
                throw new InvalidOperationException(
                    "Nie można połączyć się z Tekla Structures. Upewnij się, że Tekla jest uruchomiona.");
            }

            Drawing activeDrawing = drawingHandler.GetActiveDrawing();
            if (activeDrawing == null)
            {
                throw new InvalidOperationException(
                    "Brak aktywnego rysunku. Otwórz rysunek pojedynczej części w edytorze rysunków Tekli.");
            }

            foreach (var entry in plan.Entries)
            {
                try
                {
                    thisMoveHistory.Add((entry.Dim, entry.PreviousDistance));
                    entry.Dim.Distance = entry.NewDistance;

                    if (entry.Dim.Modify())
                    {
                        result.MovedCount++;
                        appliedNow.Add((entry.Dim, entry.NewDistance));
                    }
                    else
                    {
                        log("  Jeden wymiar R nie został zmodyfikowany (Modify() zwróciło false).");
                    }
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden wymiar R przy zapisie – błąd: " + ex.Message);
                }
            }

            activeDrawing.CommitChanges();
            log("Zapisano zmiany w rysunku (CommitChanges).");

            _undoStack.Push(thisMoveHistory);
            _lastAppliedMove = appliedNow;

            return result;
        }

        /// <summary>
        /// Zwraca skalę widoku (np. 4.0 dla rysunku szczegółowego "4:1") oraz
        /// środek jego bounding boxa - liczone raz na widok i zapamiętane w
        /// cache, żeby nie powtarzać tego samego zapytania do API dla każdego
        /// wymiaru R z tego samego widoku.
        ///
        /// RadiusDimension.Distance jest wyrażone w jednostkach MODELU, a
        /// ArcPoint1/2/3 (i to co widać na papierze) - w jednostkach PAPIERU
        /// już przeskalowanych przez widok. Potwierdzone na żywym rysunku
        /// "Einzelteil Blech": krok 100mm bez przeliczenia wylądował jako
        /// ~400mm na papierze (widok w powiększonej skali).
        ///
        /// Bezpieczny fallback = (1.0, null), jeśli nie da się odczytać
        /// widoku/skali/bounding boxa.
        /// </summary>
        private static (double scale, Point center) GetViewScaleAndCenter(
            RadiusDimension rd, Dictionary<ViewBase, (double scale, Point center)> cache, Action<string> log)
        {
            ViewBase viewBase;
            try
            {
                viewBase = rd.GetView();
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się pobrać widoku wymiaru R - błąd: " + ex.Message);
                return (1.0, null);
            }

            if (viewBase == null)
            {
                return (1.0, null);
            }

            if (cache.TryGetValue(viewBase, out var cached))
            {
                return cached;
            }

            double scale = 1.0;
            try
            {
                if (viewBase is View view)
                {
                    double s = view.Attributes.Scale;
                    if (s > 1e-6)
                    {
                        scale = s;
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się odczytać skali widoku - użyto 1.0. Błąd: " + ex.Message);
            }

            Point center = null;
            try
            {
                var box = viewBase.GetAxisAlignedBoundingBox();
                center = new Point(
                    (box.MinPoint.X + box.MaxPoint.X) / 2.0,
                    (box.MinPoint.Y + box.MaxPoint.Y) / 2.0,
                    (box.MinPoint.Z + box.MaxPoint.Z) / 2.0);
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się pobrać bounding boxa widoku - użyto domyślnego kierunku (1,0). Błąd: " + ex.Message);
            }

            log?.Invoke($"  [DIAG] Widok: skala={scale:F3}, środek widoku={(center != null ? $"({center.X:F1},{center.Y:F1})" : "brak")}");

            var value = (scale, center);
            cache[viewBase] = value;
            return value;
        }

        /// <summary>
        /// Wylicza kierunek "na zewnątrz materiału" jako (ArcPoint2 - środek
        /// widoku) znormalizowany - czyli od środka całej blachy/widoku w
        /// stronę wymiaru, ku najbliższej krawędzi. Zastąpiło wcześniejsze
        /// podejście oparte na środku okręgu łuku (center okręgu - ArcPoint2),
        /// które działało dla okrągłych otworów, ale dawało błędny (do środka
        /// materiału) kierunek dla zaokrąglonych narożników - potwierdzone na
        /// żywym rysunku "Einzelteil Blech" (dwa wymiary R lądowały na sobie
        /// na środku blachy).
        /// </summary>
        private static (double dirX, double dirY, Point referencePoint) GetOutwardDirection(RadiusDimension rd, Point viewCenter)
        {
            Point referencePoint = rd.ArcPoint2;

            double dirX = 1.0, dirY = 0.0;
            if (viewCenter != null)
            {
                double vx = referencePoint.X - viewCenter.X;
                double vy = referencePoint.Y - viewCenter.Y;
                double len = Math.Sqrt(vx * vx + vy * vy);
                if (len > 1e-6)
                {
                    dirX = vx / len;
                    dirY = vy / len;
                }
            }

            return (dirX, dirY, referencePoint);
        }

        private static double DistanceBetween(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2;
            double dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Cofa JEDEN krok ze stosu przesunięć (ostatnie kliknięcie "Przesuń"),
        /// przywracając zapamiętane wartości Distance sprzed tego kroku.
        /// Klikając "Cofnij" wielokrotnie, cofasz kolejne kroki jeden po
        /// drugim, aż do stanu sprzed pierwszego "Przesuń" w tej sesji
        /// (czyli do oryginału).
        /// </summary>
        public MoveResult UndoLastMove(Action<string> log)
        {
            var result = new MoveResult();

            if (_undoStack.Count == 0)
            {
                log("Brak zapisanej historii do cofnięcia (nie było wcześniejszego przesunięcia w tej sesji, albo już cofnięto wszystko do oryginału).");
                return result;
            }

            var lastMove = _undoStack.Pop();
            result.TotalCount = lastMove.Count;

            // Po cofnięciu nie ma już żadnego "ostatniego przesunięcia" do
            // porównywania - wyczyść bazę, żeby detekcja ręcznej zmiany nie
            // odpalała się na nieaktualnych danych.
            _lastAppliedMove = new List<(RadiusDimension, double)>();

            var drawingHandler = new DrawingHandler();
            if (!drawingHandler.GetConnectionStatus())
            {
                throw new InvalidOperationException(
                    "Nie można połączyć się z Tekla Structures. Upewnij się, że Tekla jest uruchomiona.");
            }

            Drawing activeDrawing = drawingHandler.GetActiveDrawing();
            if (activeDrawing == null)
            {
                throw new InvalidOperationException(
                    "Brak aktywnego rysunku. Otwórz rysunek pojedynczej części w edytorze rysunków Tekli.");
            }

            foreach (var entry in lastMove)
            {
                try
                {
                    entry.dim.Distance = entry.previousDistance;
                    if (entry.dim.Modify())
                    {
                        result.MovedCount++;
                    }
                    else
                    {
                        log("  Jeden wymiar R nie został przywrócony (Modify() zwróciło false).");
                    }
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden wymiar R przy cofaniu – błąd: " + ex.Message);
                }
            }

            activeDrawing.CommitChanges();
            log("Cofnięto jeden krok i zapisano rysunek (CommitChanges). Pozostało kroków do cofnięcia: " + _undoStack.Count);

            return result;
        }

        /// <summary>
        /// Sprawdza, czy któryś z wymiarów R przesuniętych ostatnim udanym
        /// "Przesuń" ma teraz Distance inne niż to, co wtedy ustawiliśmy -
        /// czyli czy ktoś ręcznie poprawił pozycję na rysunku (np. przeciągając
        /// wymiar w Tekli) od tamtego momentu. Odczyty bezpośrednio z
        /// zapamiętanych obiektów RadiusDimension, bez ponownego wyszukiwania
        /// po arkuszu - jeśli którykolwiek rzuci wyjątkiem (np. usunięty,
        /// rysunek zamknięty), traktujemy to jako "coś się zmieniło" i wolimy
        /// bezpiecznie odblokować przycisk niż zablokować użytkownika.
        /// </summary>
        public bool HasAnyDimensionChangedSinceLastMove()
        {
            if (_lastAppliedMove.Count == 0)
            {
                return false;
            }

            const double toleranceMm = 0.01;

            foreach (var entry in _lastAppliedMove)
            {
                try
                {
                    if (Math.Abs(entry.dim.Distance - entry.appliedDistance) > toleranceMm)
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
                }
            }

            return false;
        }
    }
}

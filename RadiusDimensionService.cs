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

        // Minimalna dopuszczalna odległość [mm] między szacowanym punktem
        // tekstu R a punktem wstawienia dowolnego innego tekstu na arkuszu,
        // zanim uznamy to za kolizję.
        private const double MinClearanceMm = 30.0;

        // Od jakiej odległości od łuku zaczynamy próbować.
        private const double StartDistanceMm = 15.0;

        // O ile zwiększamy dystans w każdej kolejnej próbie.
        private const double StepMm = 5.0;

        // Górny limit prób - żeby w skrajnym przypadku (np. rysunek zapchany
        // tekstami) nie uciekło w nieskończoność.
        private const double MaxDistanceMm = 300.0;

        /// <summary>
        /// EKSPERYMENTALNE: automatycznie rozstawia wszystkie wymiary R na
        /// aktywnym rysunku tak, żeby (w przybliżeniu) nie kolidowały z
        /// żadnym innym tekstem na arkuszu, bez pytania o wartość kroku.
        ///
        /// WAŻNE OGRANICZENIA - przeczytaj zanim zgłosisz "nie działa":
        /// 1. Tekla Open API nie udostępnia wprost pozycji tekstu wymiaru R
        ///    (RadiusDimension nie ma bounding boxa ani punktu wstawienia).
        ///    Pozycję tekstu SZACUJEMY geometrycznie na podstawie ArcPoint1/2/3
        ///    (środek okręgu wyliczony z tych trzech punktów) i aktualnego
        ///    Distance - to jest przybliżenie, nie odczyt z API.
        /// 2. Kolizje sprawdzamy TYLKO względem punktów wstawienia obiektów
        ///    Text na arkuszu (te akurat mają udokumentowany bounding box).
        ///    Nie sprawdzamy kolizji z innymi wymiarami R, liniami konturu,
        ///    strzałkami itd. - one nie mają bounding boxa w API.
        /// 3. Wymaga sprawdzenia na żywym rysunku. Jeśli plik się nie skompiluje
        ///    (np. inna nazwa właściwości bounding boxa niż zgadnięta), prześlij
        ///    treść błędu kompilacji - poprawka będzie szybka.
        /// </summary>
        public MoveResult AutoPlaceRadiusDimensionsAvoidingText(bool oppositeDirection, Action<string> log)
        {
            var thisMoveHistory = new List<(RadiusDimension, double)>();

            var result = new MoveResult();

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

            result.TotalCount = radiusDimensions.Count;
            log("Znaleziono " + result.TotalCount + " wymiar(ów) R oraz " + otherTextPoints.Count + " innych tekst(ów) na arkuszu.");

            foreach (var rd in radiusDimensions)
            {
                try
                {
                    double previousDistance = rd.Distance;

                    // --- LOGOWANIE DIAGNOSTYCZNE ---
                    // Cel: zebrać realne dane z żywego rysunku, żeby sprawdzić,
                    // czy założenia geometryczne (ArcPoint2 = punkt na łuku,
                    // kierunek center->ArcPoint2 = kierunek "na zewnątrz")
                    // faktycznie się zgadzają z tym, co widać na ekranie.
                    // Po teście prześlij te linie z logu z powrotem.
                    log($"  [DIAG] ArcPoint1=({rd.ArcPoint1.X:F1},{rd.ArcPoint1.Y:F1},{rd.ArcPoint1.Z:F1})");
                    log($"  [DIAG] ArcPoint2=({rd.ArcPoint2.X:F1},{rd.ArcPoint2.Y:F1},{rd.ArcPoint2.Z:F1})");
                    log($"  [DIAG] ArcPoint3=({rd.ArcPoint3.X:F1},{rd.ArcPoint3.Y:F1},{rd.ArcPoint3.Z:F1})");
                    log($"  [DIAG] Distance przed zmianą = {previousDistance:F1}");

                    Point center = TryGetCircleCenter(rd.ArcPoint1, rd.ArcPoint2, rd.ArcPoint3);
                    Point referencePoint = rd.ArcPoint2;

                    if (center == null)
                    {
                        log("  [DIAG] Środek okręgu NIE wyliczony (punkty współliniowe lub błąd) - użyto domyślnego kierunku (1,0).");
                    }
                    else
                    {
                        log($"  [DIAG] Wyliczony środek okręgu = ({center.X:F1},{center.Y:F1})");
                    }

                    double dirX = 1.0, dirY = 0.0;
                    if (center != null)
                    {
                        // POPRAWKA (na podstawie porównania danych diagnostycznych
                        // ze screenshotem "Einzelteil Träger"): kierunek, w którym
                        // faktycznie ucieka tekst R-wymiaru, to (center - arcPoint),
                        // NIE (arcPoint - center) jak było wcześniej. Wcześniejsza
                        // wersja liczyła kierunek dokładnie odwrotnie.
                        double vx = center.X - referencePoint.X;
                        double vy = center.Y - referencePoint.Y;
                        double len = Math.Sqrt(vx * vx + vy * vy);
                        if (len > 1e-6)
                        {
                            dirX = vx / len;
                            dirY = vy / len;
                        }
                    }
                    log($"  [DIAG] Kierunek na zewnątrz (dirX,dirY) = ({dirX:F3},{dirY:F3})");

                    double chosenDistance = StartDistanceMm;
                    bool foundClear = false;

                    for (double d = StartDistanceMm; d <= MaxDistanceMm; d += StepMm)
                    {
                        double signedD = oppositeDirection ? -d : d;

                        double candidateX = referencePoint.X + dirX * signedD;
                        double candidateY = referencePoint.Y + dirY * signedD;

                        bool collides = false;
                        foreach (var tp in otherTextPoints)
                        {
                            double dx = candidateX - tp.X;
                            double dy = candidateY - tp.Y;
                            double dist = Math.Sqrt(dx * dx + dy * dy);
                            if (dist < MinClearanceMm)
                            {
                                collides = true;
                                break;
                            }
                        }

                        chosenDistance = d;
                        if (!collides)
                        {
                            foundClear = true;
                            break;
                        }
                    }

                    if (!foundClear)
                    {
                        log("  Nie znaleziono w pełni wolnego miejsca w limicie " + MaxDistanceMm + "mm - ustawiono maksymalny sprawdzony dystans.");
                    }

                    double newDistance = oppositeDirection ? -chosenDistance : chosenDistance;
                    log($"  [DIAG] Wybrany dystans = {chosenDistance:F1}, nowe Distance (z uwzgl. kierunku) = {newDistance:F1}");

                    thisMoveHistory.Add((rd, previousDistance));
                    rd.Distance = newDistance;

                    if (rd.Modify())
                    {
                        result.MovedCount++;
                    }
                    else
                    {
                        log("  Jeden wymiar R nie został zmodyfikowany (Modify() zwróciło false).");
                    }
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden wymiar R – błąd: " + ex.Message);
                }
            }

            activeDrawing.CommitChanges();
            log("Zapisano zmiany w rysunku (CommitChanges).");

            _undoStack.Push(thisMoveHistory);

            return result;
        }

        /// <summary>
        /// SPRAWDZONA W PRAKTYCE metoda manualna: zwiększa Distance wszystkich
        /// wymiarów R na aktywnym rysunku o offsetMm, w kierunku zależnym od
        /// oppositeDirection. Nie próbuje zgadywać kolizji - to Ty oceniasz
        /// wzrokowo i doładowujesz kolejnymi kliknięciami.
        /// </summary>
        public MoveResult MoveAllRadiusDimensionsOutward(double offsetMm, bool oppositeDirection, Action<string> log)
        {
            var thisMoveHistory = new List<(RadiusDimension, double)>();
            var appliedNow = new List<(RadiusDimension, double)>();

            var result = new MoveResult();

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

            DrawingObjectEnumerator objectEnum = sheet.GetAllObjects();
            while (objectEnum.MoveNext())
            {
                var rd = objectEnum.Current as RadiusDimension;
                if (rd != null)
                {
                    radiusDimensions.Add(rd);
                }
            }

            result.TotalCount = radiusDimensions.Count;
            log("Znaleziono " + result.TotalCount + " wymiar(ów) R.");

            foreach (var rd in radiusDimensions)
            {
                try
                {
                    double currentDistance = rd.Distance;
                    double magnitude = Math.Abs(currentDistance) + offsetMm;
                    double newDistance = oppositeDirection ? -magnitude : magnitude;

                    // [DIAG] Tymczasowe logowanie do zdiagnozowania zgłoszenia
                    // "nawet 1mm przesuwa bardzo dużo" - pokazuje dokładne
                    // wartości Distance z API Tekli przed/po, żeby sprawdzić
                    // czy to kwestia jednostek/skali rysunku, czy błąd w kodzie.
                    log($"  [DIAG] Distance przed = {currentDistance:F3} (jednostka wg API Tekli), krok wpisany = {offsetMm:F3} mm, Distance po = {newDistance:F3}");

                    thisMoveHistory.Add((rd, currentDistance));
                    rd.Distance = newDistance;

                    if (rd.Modify())
                    {
                        result.MovedCount++;
                        appliedNow.Add((rd, newDistance));
                    }
                    else
                    {
                        log("  Jeden wymiar R nie został zmodyfikowany (Modify() zwróciło false).");
                    }
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden wymiar R – błąd: " + ex.Message);
                }
            }

            activeDrawing.CommitChanges();
            log("Zapisano zmiany w rysunku (CommitChanges).");

            _undoStack.Push(thisMoveHistory);
            _lastAppliedMove = appliedNow;

            return result;
        }

        /// <summary>
        /// Wylicza środek okręgu przechodzącego przez trzy punkty (na płaszczyźnie XY).
        /// Zwraca null, jeśli punkty są (prawie) współliniowe.
        /// </summary>
        private static Point TryGetCircleCenter(Point a, Point b, Point c)
        {
            double ax = a.X, ay = a.Y;
            double bx = b.X, by = b.Y;
            double cx = c.X, cy = c.Y;

            double d = 2.0 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(d) < 1e-9)
            {
                return null;
            }

            double ax2ay2 = ax * ax + ay * ay;
            double bx2by2 = bx * bx + by * by;
            double cx2cy2 = cx * cx + cy * cy;

            double ux = (ax2ay2 * (by - cy) + bx2by2 * (cy - ay) + cx2cy2 * (ay - by)) / d;
            double uy = (ax2ay2 * (cx - bx) + bx2by2 * (ax - cx) + cx2cy2 * (bx - ax)) / d;

            return new Point(ux, uy, a.Z);
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

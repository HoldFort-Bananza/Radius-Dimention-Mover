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
        /// Automatycznie rozstawia wszystkie wymiary R na aktywnym rysunku
        /// tak, żeby (w przybliżeniu) nie kolidowały z żadnym innym tekstem
        /// na arkuszu ANI ze sobą nawzajem, bez pytania o wartość kroku.
        ///
        /// WAŻNE OGRANICZENIA:
        /// 1. Tekla Open API nie udostępnia wprost pozycji tekstu wymiaru R
        ///    (RadiusDimension nie ma bounding boxa ani punktu wstawienia).
        ///    Pozycję tekstu SZACUJEMY geometrycznie na podstawie ArcPoint1/2/3
        ///    (środek okręgu wyliczony z tych trzech punktów) i aktualnego
        ///    Distance - to jest przybliżenie, nie odczyt z API.
        /// 2. Kolizje sprawdzamy względem punktów wstawienia obiektów Text
        ///    na arkuszu (te mają udokumentowany bounding box) oraz względem
        ///    szacowanych pozycji INNYCH wymiarów R już rozstawionych w tym
        ///    samym przebiegu. Nie sprawdzamy kolizji z liniami konturu,
        ///    strzałkami itd. - one nie mają bounding boxa w API.
        /// </summary>
        public MoveResult AutoPlaceRadiusDimensionsAvoidingText(bool oppositeDirection, Action<string> log)
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

                    // [DIAG] Tymczasowe logowanie do zdiagnozowania zgłoszenia
                    // "przesunęło na środek, dwa wymiary nachodzą na siebie" -
                    // pokazuje surowe współrzędne, żeby sprawdzić czy kierunek
                    // (center - arcPoint) jest tu poprawny.
                    log($"  [DIAG] ArcPoint1=({rd.ArcPoint1.X:F1},{rd.ArcPoint1.Y:F1})  ArcPoint2=({rd.ArcPoint2.X:F1},{rd.ArcPoint2.Y:F1})  ArcPoint3=({rd.ArcPoint3.X:F1},{rd.ArcPoint3.Y:F1})");

                    var (dirX, dirY, referencePoint) = GetOutwardDirection(rd);
                    log($"  [DIAG] referencePoint(ArcPoint2)=({referencePoint.X:F1},{referencePoint.Y:F1})  kierunek(dirX,dirY)=({dirX:F3},{dirY:F3})");

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
                        finalX = candidateX;
                        finalY = candidateY;
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

                    // chosenDistance jest w mm NA PAPIERZE (tak liczymy kolizje
                    // względem ArcPoint/Text, które też są w mm papieru).
                    // Distance zapisywane do API jest w jednostkach modelu -
                    // trzeba podzielić przez skalę widoku.
                    double scale = GetViewScale(rd, log);
                    double newDistance = (oppositeDirection ? -chosenDistance : chosenDistance) / scale;
                    log($"  Wymiar R: {previousDistance:F1} -> {newDistance:F1} (dystans {chosenDistance:F1}mm na papierze, skala widoku {scale:F3}{(foundClear ? ", wolne miejsce" : ", brak wolnego miejsca w limicie")}). [DIAG] szacowana pozycja tekstu=({finalX:F1},{finalY:F1})");

                    thisMoveHistory.Add((rd, previousDistance));
                    rd.Distance = newDistance;

                    if (rd.Modify())
                    {
                        result.MovedCount++;
                        appliedNow.Add((rd, newDistance));

                        // Kolejne wymiary R w tym samym przebiegu mają też
                        // omijać miejsce, które właśnie zajął ten - inaczej
                        // dwa sąsiednie wymiary R mogłyby wylądować na sobie.
                        otherTextPoints.Add(new Point(finalX, finalY, referencePoint.Z));
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
                    double scale = GetViewScale(rd, log);

                    // offsetMm to mm NA PAPIERZE (tak to rozumie użytkownik).
                    // Distance jest w jednostkach modelu, więc krok trzeba
                    // podzielić przez skalę widoku przed dodaniem.
                    double magnitude = Math.Abs(currentDistance) + offsetMm / scale;
                    double newDistance = oppositeDirection ? -magnitude : magnitude;

                    log($"  [DIAG] Distance przed = {currentDistance:F3} (jedn. modelu), skala widoku = {scale:F3}, krok wpisany = {offsetMm:F3} mm (papier) = {offsetMm / scale:F3} (jedn. modelu), Distance po = {newDistance:F3}, ~papier po = {Math.Abs(newDistance) * scale:F1}mm");

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
        /// Wylicza kierunek "na zewnątrz" (w stronę, w którą realnie ucieka
        /// tekst wymiaru R) oraz punkt odniesienia (ArcPoint2) dla danego
        /// wymiaru R. Współdzielone przez auto-rozstawianie i sprawdzanie
        /// kolizji przy ręcznym kroku, żeby liczyć to samo tak samo.
        /// </summary>
        private static (double dirX, double dirY, Point referencePoint) GetOutwardDirection(RadiusDimension rd)
        {
            Point center = TryGetCircleCenter(rd.ArcPoint1, rd.ArcPoint2, rd.ArcPoint3);
            Point referencePoint = rd.ArcPoint2;

            double dirX = 1.0, dirY = 0.0;
            if (center != null)
            {
                double vx = center.X - referencePoint.X;
                double vy = center.Y - referencePoint.Y;
                double len = Math.Sqrt(vx * vx + vy * vy);
                if (len > 1e-6)
                {
                    dirX = vx / len;
                    dirY = vy / len;
                }
            }

            return (dirX, dirY, referencePoint);
        }

        /// <summary>
        /// Zwraca skalę widoku, w którym leży dany wymiar R (np. 4.0 dla
        /// rysunku szczegółowego "4:1"). RadiusDimension.Distance jest
        /// wyrażone w jednostkach MODELU, a ArcPoint1/2/3 (i to co widać na
        /// papierze) - w jednostkach PAPIERU już przeskalowanych przez widok.
        /// Bez tego przeliczenia krok w mm wpisany przez użytkownika (myślany
        /// jako mm NA PAPIERZE) wychodził kilkukrotnie za duży/za mały,
        /// zależnie od skali widoku - potwierdzone na żywym rysunku
        /// "Einzelteil Blech" (widok w powiększonej skali), gdzie krok 100mm
        /// wylądował jako ~400mm na papierze.
        ///
        /// Bezpieczny fallback = 1.0 (stare, "1mm = 1mm" zachowanie), jeśli z
        /// jakiegoś powodu nie da się odczytać widoku/skali.
        /// </summary>
        private static double GetViewScale(RadiusDimension rd, Action<string> log)
        {
            try
            {
                var viewBase = rd.GetView();
                if (viewBase is View view)
                {
                    double scale = view.Attributes.Scale;
                    if (scale > 1e-6)
                    {
                        return scale;
                    }

                    log?.Invoke($"  [DIAG] Skala widoku odczytana jako {scale:F3} (nieprawidłowa) - użyto 1.0.");
                }
                else
                {
                    log?.Invoke("  [DIAG] GetView() nie zwrócił obiektu typu View - użyto skali 1.0.");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się odczytać skali widoku - użyto 1.0. Błąd: " + ex.Message);
            }

            return 1.0;
        }

        private static double DistanceBetween(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2;
            double dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Sprawdza (bez wprowadzania żadnych zmian na rysunku), czy ręczne
        /// przesunięcie wszystkich wymiarów R o offsetMm spowodowałoby
        /// kolizję - z innym tekstem na arkuszu albo z innym wymiarem R,
        /// który też przesunąłby się o ten sam krok. Używane, żeby ostrzec
        /// "power usera" PRZED wykonaniem ręcznego przesunięcia, zamiast po
        /// fakcie każąc mu to poprawiać.
        /// </summary>
        public bool WouldManualMoveCollide(double offsetMm, bool oppositeDirection)
        {
            var drawingHandler = new DrawingHandler();
            if (!drawingHandler.GetConnectionStatus())
            {
                return false;
            }

            Drawing activeDrawing = drawingHandler.GetActiveDrawing();
            if (activeDrawing == null)
            {
                return false;
            }

            var sheet = activeDrawing.GetSheet();
            if (sheet == null)
            {
                return false;
            }

            var radiusDimensions = new List<RadiusDimension>();
            var textPoints = new List<Point>();

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
                        var box = txt.GetAxisAlignedBoundingBox();
                        textPoints.Add(new Point(
                            (box.MinPoint.X + box.MaxPoint.X) / 2.0,
                            (box.MinPoint.Y + box.MaxPoint.Y) / 2.0,
                            (box.MinPoint.Z + box.MaxPoint.Z) / 2.0));
                    }
                    catch
                    {
                        // Pomiń Text bez odczytywalnego bounding boxa - to tylko
                        // podgląd "czy będzie kolizja", nie krytyczna operacja.
                    }
                }
            }

            var prospective = new List<(double x, double y)>();
            foreach (var rd in radiusDimensions)
            {
                try
                {
                    double currentDistance = rd.Distance;
                    double scale = GetViewScale(rd, null);

                    // Ten sam przelicznik co w MoveAllRadiusDimensionsOutward:
                    // currentDistance jest w jedn. modelu, przeliczamy na mm
                    // papieru (skala), dodajemy krok (już w mm papieru).
                    double magnitudePaper = Math.Abs(currentDistance) * scale + offsetMm;
                    double signedD = oppositeDirection ? -magnitudePaper : magnitudePaper;

                    var (dirX, dirY, referencePoint) = GetOutwardDirection(rd);
                    double candidateX = referencePoint.X + dirX * signedD;
                    double candidateY = referencePoint.Y + dirY * signedD;
                    prospective.Add((candidateX, candidateY));
                }
                catch
                {
                    // Pomiń pojedynczy wymiar, którego nie da się policzyć -
                    // to tylko podgląd, prawdziwy ruch i tak obsłuży błędy.
                }
            }

            for (int i = 0; i < prospective.Count; i++)
            {
                foreach (var tp in textPoints)
                {
                    if (DistanceBetween(prospective[i].x, prospective[i].y, tp.X, tp.Y) < MinClearanceMm)
                    {
                        return true;
                    }
                }

                for (int j = i + 1; j < prospective.Count; j++)
                {
                    if (DistanceBetween(prospective[i].x, prospective[i].y, prospective[j].x, prospective[j].y) < MinClearanceMm)
                    {
                        return true;
                    }
                }
            }

            return false;
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

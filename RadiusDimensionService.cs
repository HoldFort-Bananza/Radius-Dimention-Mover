using System;
using System.Collections.Generic;
using System.Threading;
using Tekla.Structures.Drawing;

namespace RadiusDimensionMover
{
    public class MoveResult
    {
        public int TotalCount;
        public int MovedCount;
    }

    /// <summary>
    /// Cała logika rozstawiania wymiarów promieni. Nie zna UI - komunikuje się
    /// przez Action&lt;string&gt; (log) i zwracany MoveResult.
    ///
    /// Działa WYŁĄCZNIE na danych z Tekla Open API: współrzędnych łuku,
    /// bryle części z modelu i geometrii linii wymiarowych. Nie robi zrzutów
    /// ekranu i nie analizuje pikseli (wcześniejsza wersja to robiła - została
    /// usunięta).
    ///
    /// ⚠️ NAJWAŻNIEJSZA PUŁAPKA - JEDNOSTKI:
    ///   ArcPoint1/2/3, bryła części, punkty linii wymiarowych -> MODEL
    ///   RadiusDimension.Distance                              -> PAPIER
    /// Dwie różne jednostki w tej samej klasie. Liczymy w modelu i dzielimy
    /// przez skalę widoku przed zapisem. Pomyłka daje błąd równy skali rysunku
    /// (na 1:5 pięciokrotny) i była przyczyną większości problemów w historii
    /// tego projektu.
    ///
    /// Zanim zmienisz sposób rozstawiania, przeczytaj listę ślepych uliczek -
    /// dziewięć podejść zostało już przetestowanych i odrzuconych, z pomiarami:
    /// https://github.com/HoldFort-Bananza/Radius-Dimention-Mover/wiki/4-Slepe-uliczki
    /// </summary>
    public class RadiusDimensionService
    {
        // --- Parametry wyszukiwania wolnego miejsca dla trybu AWARYJNEGO
        // (Placing=Free) - w jednostkach MODELU, tak jak Distance. ---
        private const double SearchMarginMm = 30.0;
        private const double MinimalDistanceMm = 15.0;
        private const double MaximalDistanceMm = 300.0;

        // Mały "neutralny" krok używany tylko po to, żeby wymusić świeże
        // przeliczenie przez Teklę przy przełączaniu Fixed -> Free (patrz
        // komentarz w PlaceUsingFreeMode).
        private const double ResetDistanceMm = 4.0;

        // --- Rozstawianie wymiarów R: WYŁĄCZNIE ze współrzędnych z API.
        // Żadnych zrzutów ekranu ani analizy pikseli - patrz
        // TryPlaceByGeometry().
        //
        // UWAGA NA JEDNOSTKI - to najczęstsze źródło błędów w tym projekcie:
        //   ArcPoint1/2/3, bryła części, punkty linii wymiarowych  -> MODEL
        //   RadiusDimension.Distance                               -> PAPIER
        // Czyli DWIE różne jednostki w tej samej klasie. Liczymy w modelu, a
        // przed zapisem dzielimy przez skalę widoku (patrz paperDistance).

        // Znak Distance oznaczający kierunek NA ZEWNĄTRZ (od środka łuku).
        // Ustalone empirycznie: RadiusDimension nie udostępnia swojej
        // pozycji, więc konwencji nie da się odczytać z API, ale kilkanaście
        // niezależnych pomiarów na trzech rysunkach dało za każdym razem ten
        // sam wynik. Jeśli kiedyś wyjdzie odwrotnie - wystarczy tu -1/+1.
        private const double OutwardSign = -1.0;

        // --- Kiedy tekst wymiaru może zostać WEWNĄTRZ części ---
        // Warunki: część NIE MA żadnego otworu I wokół łuku jest w płaszczyźnie
        // blachy realne miejsce. "Jest miejsce" mierzymy KRÓTSZYM wymiarem
        // płaszczyzny (bez grubości) w stosunku do promienia łuku.
        //
        // Pierwotnie był tu stały próg 300mm na NAJWIĘKSZYM wymiarze bryły i
        // to było złe kryterium: blacha 65,5 x 180,8 bez otworów, w której
        // tekst spokojnie się mieścił, była wyrzucana na zewnątrz, bo 180,8
        // nie przechodziło progu. Grubość i długość nie mówią nic o tym, czy
        // tekst ma się gdzie zmieścić - mówi o tym krótszy wymiar płaszczyzny.
        private const double InsideRoomRadiusFactor = 3.0;

        // Dodatkowy, BEZWZGLĘDNY próg na krótszy wymiar płaszczyzny - zgrubny
        // filtr "czy blacha jest dość szeroka, żeby tekst miał gdzie usiąść".
        //
        // 60mm = wartość wybrana przez użytkownika. Historycznie próg musiał
        // być znacznie wyższy (120mm), bo Distance było zapisywane w złych
        // jednostkach i tekst przelatywał na drugą stronę części. Po poprawce
        // jednostek to już nie jest ta linia obrony - właściwym
        // zabezpieczeniem jest sprawdzenie, czy droga do środka nie przecina
        // innego wymiaru (patrz TryPlaceByGeometry).
        private const double InsideMinShortFaceMm = 60.0;

        // Jak głęboko w część wchodzi tekst, gdy wolno mu tam zostać -
        // ułamek KRÓTSZEGO wymiaru płaszczyzny, żeby tekst nie wyszedł
        // obrysem po przeciwnej stronie.
        private const double InsideFraction = 0.35;

        // Prześwit ZA najdalszą linią wymiarową, gdy tekst idzie na zewnątrz -
        // w mm NA PAPIERZE, bo o czytelność na papierze tu chodzi i taką
        // jednostkę przyjmuje Distance.
        //
        // Odległość na zewnątrz NIE jest ułamkiem rozmiaru części: to, jak
        // daleko trzeba odejść, zależy od tego, dokąd sięga opis elementu, a
        // nie od tego, jak duża jest blacha. Ułamek rozmiaru dawał na blachy
        // 175mm 3,5mm na papierze - tekst siedział na konturze i na wymiarach.
        private const double OutsideClearancePaperMm = 8.0;

        // Gdy w kierunku "na zewnątrz" nie ma żadnej linii wymiarowej - o tyle
        // (mm na papierze) odsuwamy tekst od łuku.
        private const double OutsideFallbackPaperMm = 10.0;

        // Szerokość korytarza (ułamek krótszego wymiaru płaszczyzny), w którym
        // szukamy linii wymiarowych na drodze "na zewnątrz".
        private const double OutsideCorridorFraction = 0.5;

        // Kierunki pod 45 stopni (zaokrąglony narożnik) mają |DirX| ≈ |DirY|.
        // Przy takim remisie grupa wyrównania musi być wybrana
        // DETERMINISTYCZNIE, inaczej ten sam rysunek raz się wyrównuje, a raz
        // nie. Wartość poniżej 1 przeciąga remis na stronę pionu - patrz
        // GroupKey.
        private const double DiagonalTieTolerance = 0.999;

        // Jak długi odcinek linii odniesienia sprawdzamy pod kątem kolizji z
        // opisami - jako wielokrotność rozmiaru części, DODANA do Distance.
        // Hojnie, bo API nie podaje pozycji tekstu wymiaru, a przeszacowanie
        // jest tu tanie: opis leżący dalej i tak zostanie odsunięty tylko w
        // bok i tylko o brakującą różnicę, więc zostaje przy swoim otworze.
        private const double LeaderCheckLengthFactor = 1.5;

        // Minimalny prześwit między opisem (Mark) a linią odniesienia wymiaru
        // R, ponad połowę przekątnej opisu (mm w modelu). Odsunięcie ma być
        // delikatne - tyle, żeby się nie nachodziły.
        private const double MarkClearanceMm = 12.0;

        // --- Parametry "dociągania" opisów (Mark, np. "1*Ø13") bliżej -
        // domyślnie mają MaximalDistance=0 (bez limitu), przez co potrafią
        // wylądować bardzo daleko od tego, co opisują. Ograniczamy zakres,
        // żeby zostały blisko, ale wciąż z dala od kolizji. ---
        private const double MarkSearchMarginMm = 15.0;
        private const double MarkMinimalDistanceMm = 10.0;
        private const double MarkMaximalDistanceMm = 60.0;

        /// <summary>
        /// AUTOMATYCZNE rozstawianie wszystkich wymiarów R na aktywnym
        /// rysunku, unikając kolizji z innymi elementami - używając
        /// WBUDOWANEGO w Teklę silnika auto-rozstawiania wymiarów
        /// (RadiusDimensionAttributes.Placing = Placings.Free), a nie
        /// własnych zgadywanek.
        ///
        /// Znalezione po tym, jak wcześniejsze próby (patrz historia w git)
        /// zawiodły: RadiusDimension nie ma żadnej metody do odczytania
        /// swojej rzeczywistej pozycji na rysunku (ArcPoint1/2/3 są stałe,
        /// brak IAxisAlignedBoundingBox, GetRelatedObjects()/GetDimensionSet()
        /// nic nie dają) - więc nie da się zbudować własnego, niezawodnego
        /// wykrywania kolizji. Okazało się jednak, że Tekla ma do tego
        /// WŁASNY, wbudowany mechanizm (ten sam co przy StraightDimensionSet
        /// - "Placing: Free/Fixed"), który po prostu trzeba było znaleźć w
        /// atrybutach (odziedziczonych z DimensionSetBaseAttributes, nie
        /// zadeklarowanych bezpośrednio na RadiusDimensionAttributes - stąd
        /// wcześniej przeoczone).
        ///
        /// WAŻNE - dwuetapowość jest konieczna: samo ustawienie Placing=Free
        /// z nowymi parametrami wyszukiwania NIE wymusza ponownego
        /// przeliczenia, jeśli wymiar był już wcześniej w trybie Free
        /// (Tekla zdaje się cache'ować wynik). Trzeba najpierw przełączyć na
        /// Fixed z dowolnym Distance, zapisać, i DOPIERO wtedy przełączyć
        /// na Free ze świeżymi parametrami - potwierdzone empirycznie na
        /// żywym rysunku (dwa wymiary R, oba wylądowały w czystych,
        /// nienachodzących na siebie ani na inne elementy miejscach).
        ///
        /// WAŻNE - jednostki: Distance jest w mm NA PAPIERZE, mimo że
        /// ArcPoint1/2/3 w tej samej klasie są w jednostkach MODELU. Liczymy
        /// w modelu i dzielimy przez skalę widoku przed zapisem. Pomyłka tutaj
        /// daje błąd równy skali rysunku (na 1:5 pięciokrotny) i objawia się
        /// tekstem lądującym po drugiej stronie części.
        /// </summary>
        public MoveResult AutoPlaceWithCollisionAvoidance(Action<string> log)
        {
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
            var marks = new List<Mark>();
            DrawingObjectEnumerator objectEnum = sheet.GetAllObjects();
            while (objectEnum.MoveNext())
            {
                var current = objectEnum.Current;
                if (current is RadiusDimension rd)
                {
                    radiusDimensions.Add(rd);
                }
                else if (current is Mark mark)
                {
                    marks.Add(mark);
                }
            }

            result.TotalCount = radiusDimensions.Count;
            log("Znaleziono " + result.TotalCount + " wymiar(ów) R oraz " + marks.Count + " opis(ów) (Mark).");

            var scaleCache = new Dictionary<ViewBase, double>();

            // Kolejność ma znaczenie i jest przemyślana: najpierw opisy
            // dociągane są automatem Tekli (żeby nie wisiały daleko), potem
            // wymiary R lądują na policzonych pozycjach, a na końcu te opisy,
            // które trafiły na linię odniesienia wymiaru, są DELIKATNIE
            // odsuwane w bok. Każdy etap to jedno przejście po obiektach -
            // żadnego szukania po kroku ani prób "aż się uda".
            TightenMarks(marks, scaleCache, log);
            activeDrawing.CommitChanges();

            // Linie wymiarowe jako przeszkody. Zbierane TERAZ, przed
            // rozstawianiem wymiarów R, bo są potrzebne w dwóch miejscach:
            // (1) żeby nie wpuścić tekstu wymiaru R do środka części, jeśli
            //     droga tam przecina inny wymiar,
            // (2) żeby nie odsunąć opisu prosto na linię wymiarową.
            // Linie wymiarów prostych nie zmieniają się przy rozstawianiu
            // wymiarów R, więc jeden odczyt na starcie wystarcza.
            var obstacles = new List<Segment>();
            if (radiusDimensions.Count > 0)
            {
                try
                {
                    ViewBase view = radiusDimensions[0].GetView();
                    if (view != null)
                    {
                        obstacles = CollectDimensionLines(view, log);
                    }
                }
                catch (Exception ex)
                {
                    log("  [DIAG] Nie udało się zebrać linii wymiarowych: " + ex.Message);
                }
            }
            log("  Wykryto " + obstacles.Count + " lini(i) wymiarowych jako przeszkody.");

            // Rozstawianie wymiarów R w trzech krokach: policz wszystkie
            // położenia, WYRÓWNAJ te idące w tę samą stronę, i dopiero wtedy
            // zapisz. Wyrównanie wymaga znajomości wszystkich planów naraz,
            // dlatego liczenie jest oddzielone od zapisu.
            var plans = new List<PlacementPlan>();

            foreach (var rd in radiusDimensions)
            {
                try
                {
                    double scale = GetViewScale(rd, scaleCache, log);

                    var plan = ComputePlan(rd, obstacles, scale, log);
                    if (plan == null)
                    {
                        // Geometria się nie udała (np. zdegenerowany łuk) -
                        // zostaje wbudowany silnik Tekli jako wariant
                        // awaryjny, żeby program zawsze coś zrobił.
                        if (PlaceUsingFreeMode(rd, activeDrawing, scale, log))
                        {
                            result.MovedCount++;
                        }
                        else
                        {
                            log("  Jeden wymiar R nie został zmodyfikowany (Modify() zwróciło false).");
                        }
                        continue;
                    }

                    plans.Add(plan);
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden wymiar R – błąd: " + ex.Message);
                }
            }

            AlignPlans(plans, log);

            // Promienie linii odniesienia - potrzebne w ostatnim etapie, żeby
            // wiedzieć, czego opisy mają nie zasłaniać.
            var leaderRays = new List<LeaderRay>();

            foreach (var plan in plans)
            {
                try
                {
                    leaderRays.Add(ApplyPlan(plan, log));
                    result.MovedCount++;
                }
                catch (Exception ex)
                {
                    log("  Nie udało się zapisać jednego wymiaru R – błąd: " + ex.Message);
                }
            }

            activeDrawing.CommitChanges();

            NudgeMarksOffLeaders(marks, leaderRays, obstacles, log);
            activeDrawing.CommitChanges();

            return result;
        }

        /// <summary>
        /// Półprosta, po której biegnie linia odniesienia (leader) wymiaru R:
        /// startuje na łuku i idzie na zewnątrz. Tekst wymiaru siedzi na jej
        /// końcu. Opisy (Mark) mają na niej nie leżeć.
        /// </summary>
        private class LeaderRay
        {
            public double OriginX;
            public double OriginY;
            public double DirX;
            public double DirY;
            public double Length;
        }

        /// <summary>
        /// Policzone położenie JEDNEGO wymiaru R - jeszcze niezapisane.
        ///
        /// Rozdzielenie „policz" od „zapisz" jest potrzebne, żeby przed
        /// zapisem móc WYRÓWNAĆ wymiary idące w tę samą stronę (patrz
        /// AlignPlans) - bez tego każdy wymiar lądowałby na własnej wysokości
        /// i rysunek wyglądałby niechlujnie.
        /// </summary>
        private class PlacementPlan
        {
            public RadiusDimension Dimension;
            public double Scale;

            // Punkt na łuku, od którego biegnie linia odniesienia (model).
            public double ArcX, ArcY;

            // Kierunek linii odniesienia - jednostkowy (model).
            public double DirX, DirY;

            // Odległość tekstu od łuku w jednostkach MODELU, bez znaku.
            public double DistanceModel;

            // Czy tekst ma iść do wnętrza części.
            public bool Inside;

            // -1 dla łuku WKLĘSŁEGO, +1 dla wypukłego. Odwraca znak zapisanego
            // Distance, bo to znak decyduje, po której stronie łuku Tekla
            // postawi tekst. Ustalone empirycznie: Distance ujemny wysyła
            // tekst w kierunku środek->łuk, dodatni w przeciwnym.
            public double OutwardFlip = 1.0;

            // Rozmiar części - do wyliczenia długości sprawdzanego leadera.
            public double FaceLongMm;
        }

        /// <summary>
        /// Liczy położenie JEDNEGO wymiaru R, nic nie zapisując.
        ///
        /// Wszystko wyłącznie ze współrzędnych z API Tekli - żadnych zrzutów
        /// ekranu, żadnego szukania po kroku. Wejście (w jednostkach MODELU):
        /// ArcPoint1/2/3 -> środek i promień łuku (circumcenter), bryła części
        /// z modelu -> rozmiar i liczba otworów, linie wymiarowe -> przeszkody.
        ///
        /// Zasada rozstawiania (ustalona z użytkownikiem):
        /// - część BEZ otworów, dostatecznie szeroka, i droga do środka nie
        ///   przecina innego wymiaru -> tekst zostaje wewnątrz części,
        /// - w każdym innym przypadku -> tekst na zewnątrz, tuż za tym, dokąd
        ///   sięgają linie wymiarowe opisujące element.
        ///
        /// Zwraca null, gdy geometrii nie da się policzyć (zdegenerowany łuk,
        /// brak danych z modelu) - wtedy wywołujący spada do
        /// PlaceUsingFreeMode.
        /// </summary>
        private PlacementPlan ComputePlan(
            RadiusDimension rd, List<Segment> obstacles, double scale, Action<string> log)
        {
            Tekla.Structures.Geometry3d.Point center;
            try
            {
                center = CircumCenter(rd.ArcPoint1, rd.ArcPoint2, rd.ArcPoint3);
            }
            catch (Exception ex)
            {
                log("  Nie udało się policzyć środka łuku: " + ex.Message);
                return null;
            }

            double radius = Distance2D(center, rd.ArcPoint2);
            if (radius < 1e-6)
            {
                log("  Zdegenerowana geometria łuku (promień ~0).");
                return null;
            }

            // Kierunek "na zewnątrz": od środka okręgu przez łuk. Dla
            // wypukłego zaokrąglenia narożnika środek leży po stronie
            // materiału, więc ten kierunek wychodzi z części.
            double dirX = (rd.ArcPoint2.X - center.X) / radius;
            double dirY = (rd.ArcPoint2.Y - center.Y) / radius;

            var facts = GetPartFacts(rd, log);
            if (!facts.Valid)
            {
                log("  Nie udało się odczytać danych części z modelu.");
                return null;
            }

            // Łuk WKLĘSŁY (wcięcie) ma środek okręgu w pustce, nie w
            // materiale, więc kierunek środek->łuk wchodzi w część zamiast z
            // niej wychodzić. Trzeba go odwrócić - patrz CollectRoundingShapes.
            bool concave = facts.IsConcaveRadius(radius);

            bool roomInside = facts.FaceShortMm >= radius * InsideRoomRadiusFactor
                && facts.FaceShortMm >= InsideMinShortFaceMm;

            // Przy wcięciu "wewnątrz części" traci sens: to, co leży po
            // stronie środka okręgu, jest pustką poza obrysem, a nie wnętrzem
            // blachy. Taki wymiar zawsze idzie na zewnątrz.
            bool insideAllowed = facts.HoleCount == 0 && roomInside && !concave;

            log("  Część: płaszczyzna " + facts.FaceShortMm.ToString("0") + " x " + facts.FaceLongMm.ToString("0")
                + "mm, śruby: " + facts.BoltCount
                + ", wycięcia: " + facts.TotalCutCount + " (okrągłych: " + facts.RoundCutCount + ")"
                + ", promień łuku " + radius.ToString("0") + "mm"
                + (concave ? " (łuk WKLĘSŁY - kierunek odwrócony)" : "") + ".");

            // Zanim wpuścimy tekst do środka: czy droga tam nie przecina
            // INNEGO WYMIARU? Same warunki rozmiarowe tego nie wyłapują - na
            // blachy 66 x 181 tekst lądował dokładnie na wymiarze długości,
            // bo linia odniesienia biegnie z narożnika na skos przez część i
            // wychodzi jej dolną krawędzią.
            if (insideAllowed)
            {
                var insideRay = new LeaderRay
                {
                    OriginX = center.X - dirX * radius,
                    OriginY = center.Y - dirY * radius,
                    DirX = -dirX,
                    DirY = -dirY,
                    Length = facts.FaceShortMm * InsideFraction
                             + facts.FaceLongMm * LeaderCheckLengthFactor
                };

                if (CrossesAnySegment(insideRay, obstacles))
                {
                    insideAllowed = false;
                    log("  Droga do środka przecina inny wymiar - tekst idzie na zewnątrz.");
                }
            }

            // Od tego miejsca dirOutX/dirOutY to kierunek, w którym tekst
            // FAKTYCZNIE ma pójść, żeby wyjść z części. Dla wypukłego to
            // środek->łuk, dla wklęsłego dokładnie odwrotnie.
            double flip = concave ? -1.0 : 1.0;
            double dirOutX = dirX * flip;
            double dirOutY = dirY * flip;

            double distanceModel;
            if (insideAllowed)
            {
                distanceModel = facts.FaceShortMm * InsideFraction;
                log("  -> tekst WEWNĄTRZ części.");
            }
            else
            {
                // Na zewnątrz: tuż ZA najdalszą linią wymiarową leżącą w tym
                // kierunku. Liczone z rzeczywistych współrzędnych linii, nie z
                // rozmiaru części - to dwie różne rzeczy.
                double reachModel = OutermostDimensionReach(
                    center.X + dirX * radius, center.Y + dirY * radius,
                    dirOutX, dirOutY,
                    facts.FaceShortMm * OutsideCorridorFraction,
                    obstacles);

                distanceModel = reachModel > 0
                    ? reachModel + OutsideClearancePaperMm * scale
                    : OutsideFallbackPaperMm * scale;

                log("  -> tekst NA ZEWNĄTRZ: linie wymiarowe sięgają "
                    + (reachModel > 0 ? reachModel.ToString("0") + "mm" : "(brak w tym kierunku)")
                    + ", prześwit " + OutsideClearancePaperMm.ToString("0") + "mm na papierze.");
            }

            // Kierunek zapisany w planie to kierunek, w którym FAKTYCZNIE
            // pójdzie tekst - dla wariantu wewnętrznego przeciwny do "na
            // zewnątrz".
            // Wariant wewnętrzny zaczepia się na PRZECIWNYM biegunie okręgu
            // i idzie w stronę materiału - stąd osobny znak. Wariant
            // zewnętrzny zaczepia się na łuku i idzie kierunkiem dirOut.
            double sign = insideAllowed ? -1.0 : 1.0;

            return new PlacementPlan
            {
                Dimension = rd,
                Scale = scale,
                ArcX = center.X + dirX * radius * sign,
                ArcY = center.Y + dirY * radius * sign,
                DirX = insideAllowed ? -dirX : dirOutX,
                DirY = insideAllowed ? -dirY : dirOutY,
                DistanceModel = distanceModel,
                Inside = insideAllowed,
                OutwardFlip = insideAllowed ? 1.0 : flip,
                FaceLongMm = facts.FaceLongMm
            };
        }

        /// <summary>
        /// WYRÓWNUJE wymiary idące w tę samą stronę, żeby ich teksty leżały w
        /// jednej linii - bez tego dwa wymiary wyrzucone „do góry" lądują na
        /// różnych wysokościach i rysunek wygląda niechlujnie.
        ///
        /// Jak to działa:
        /// 1. Plany są grupowane po **dominującym kierunku** tekstu (góra,
        ///    dół, lewo, prawo) - bierzemy większą składową wektora kierunku.
        /// 2. W grupie „góra"/„dół" wyrównujemy współrzędną **Y** tekstów, w
        ///    grupie „lewo"/„prawo" współrzędną **X**.
        /// 3. Docelową wartością jest ta NAJDALSZA w grupie, więc żaden wymiar
        ///    nie zostaje przyciągnięty bliżej, niż wynikało z jego własnego
        ///    wyliczenia. To ważne: każda odległość została policzona tak, żeby
        ///    ominąć przeszkody w swoim kierunku, a skracanie mogłoby ją
        ///    wepchnąć z powrotem na linię wymiarową.
        ///
        /// Pozycja tekstu jest przybliżana jako `łuk + kierunek × odległość` -
        /// API nie podaje jej wprost, ale do wyrównania między sobą to
        /// wystarcza, bo błąd modelu jest dla wszystkich wymiarów ten sam.
        ///
        /// Wymiary umieszczone WEWNĄTRZ części są pomijane - tam nie chodzi o
        /// równy szereg, a o zmieszczenie się w obrysie.
        /// </summary>
        /// <summary>
        /// Klucz grupy wyrównania: 'G'óra, 'D'ół, 'L'ewo, 'P'rawo.
        ///
        /// UWAGA - TU BYŁ BŁĄD. Zaokrąglony narożnik blachy daje kierunek
        /// dokładnie pod 45 stopni, czyli |DirX| i |DirY| są sobie RÓWNE z
        /// dokładnością do szumu zmiennoprzecinkowego. Zwykłe porównanie
        /// `Math.Abs(DirY) >= Math.Abs(DirX)` rozstrzygało wtedy losowo i ten
        /// sam rysunek przy kolejnych uruchomieniach trafiał do innych grup:
        /// raz dwie grupy po dwa wymiary (wyrównanie działało), raz cztery
        /// grupy po jednym (nie działało nic).
        ///
        /// Remis rozstrzygamy więc jawnie na korzyść PIONU. Powód nie jest
        /// dowolny: łańcuchy wymiarowe biegną zwykle nad i pod częścią, więc
        /// to wspólna WYSOKOŚĆ tekstów jest tym, co widać na rysunku. Zgodne
        /// z tym, jak układ ustawia człowiek - na [31339] ręcznie ustawione
        /// teksty miały zgodne Y (167,7 i 166,7 dla pary R5), a X różne.
        ///
        /// Tolerancja jest RELATYWNA, bo kierunki są jednostkowe, ale różnice
        /// biorą się z liczenia środka okręgu i sięgają rzędu 1e-4.
        /// </summary>
        private static char GroupKey(double dirX, double dirY)
        {
            double ax = Math.Abs(dirX);
            double ay = Math.Abs(dirY);

            bool vertical = ay >= ax * DiagonalTieTolerance;

            return vertical
                ? (dirY > 0 ? 'G' : 'D')
                : (dirX > 0 ? 'P' : 'L');
        }

        private static void AlignPlans(List<PlacementPlan> plans, Action<string> log)
        {
            // Klucz grupy: 'G'/'D' dla pionu, 'L'/'P' dla poziomu.
            var groups = new Dictionary<char, List<PlacementPlan>>();

            foreach (var p in plans)
            {
                if (p.Inside)
                {
                    continue;
                }

                char key = GroupKey(p.DirX, p.DirY);

                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<PlacementPlan>();
                    groups[key] = list;
                }
                list.Add(p);
            }

            foreach (var kv in groups)
            {
                var group = kv.Value;
                if (group.Count < 2)
                {
                    continue;   // nie ma z czym wyrównywać
                }

                bool vertical = kv.Key == 'G' || kv.Key == 'D';

                // Docelowa współrzędna = najdalsza w grupie.
                double target = 0;
                bool first = true;
                foreach (var p in group)
                {
                    double coord = vertical
                        ? p.ArcY + p.DirY * p.DistanceModel
                        : p.ArcX + p.DirX * p.DistanceModel;

                    // "Najdalsza" znaczy najdalej w kierunku grupy, więc dla
                    // kierunków ujemnych to najmniejsza wartość.
                    bool farther = first
                        || (vertical
                            ? (kv.Key == 'G' ? coord > target : coord < target)
                            : (kv.Key == 'P' ? coord > target : coord < target));

                    if (farther)
                    {
                        target = coord;
                        first = false;
                    }
                }

                int aligned = 0;
                foreach (var p in group)
                {
                    double component = vertical ? p.DirY : p.DirX;
                    if (Math.Abs(component) < 1e-6)
                    {
                        continue;   // kierunek prostopadły - nie da się wyrównać
                    }

                    double origin = vertical ? p.ArcY : p.ArcX;
                    double needed = (target - origin) / component;

                    // Nigdy nie skracamy - patrz opis metody.
                    if (needed > p.DistanceModel)
                    {
                        p.DistanceModel = needed;
                        aligned++;
                    }
                }

                if (aligned > 0)
                {
                    log("  Wyrównano " + group.Count + " wymiar(y) idące w stronę "
                        + DirectionName(kv.Key) + " do jednej linii.");
                }
            }
        }

        private static string DirectionName(char key)
        {
            switch (key)
            {
                case 'G': return "góry";
                case 'D': return "dołu";
                case 'L': return "lewej";
                default: return "prawej";
            }
        }

        /// <summary>
        /// Zapisuje policzony plan do Tekli i zwraca półprostą linii
        /// odniesienia (do późniejszego odsuwania opisów).
        ///
        /// WAŻNE - jednostki: Distance jest w mm NA PAPIERZE, a plan trzyma
        /// odległość w jednostkach MODELU (jak ArcPoint1/2/3), więc tu jest
        /// dzielenie przez skalę widoku. Pomyłka daje błąd równy skali rysunku
        /// i była przyczyną większości problemów w historii tego projektu.
        ///
        /// Tryb Fixed, nie Free - tylko wtedy nasza wartość jest respektowana.
        /// </summary>
        private static LeaderRay ApplyPlan(PlacementPlan p, Action<string> log)
        {
            // OutwardFlip odwraca znak dla łuków wklęsłych - bez tego tekst
            // wcięcia leci w materiał. Patrz CollectRoundingShapes.
            double paperDistance = OutwardSign * (p.Inside ? -1.0 : 1.0) * p.OutwardFlip
                * p.DistanceModel / p.Scale;

            var attrs = p.Dimension.Attributes;
            attrs.Placing = new DimensionSetBaseAttributes.DimensionPlacingAttributes(
                DimensionSetBaseAttributes.Placings.Fixed,
                new PlacingDirectionAttributes(true, true),
                new PlacingDistanceAttributes(2.0, Math.Abs(paperDistance)));
            p.Dimension.Attributes = attrs;
            p.Dimension.Distance = paperDistance;
            p.Dimension.Modify();

            log("     (Distance na papierze = " + paperDistance.ToString("0.#")
                + " przy skali " + p.Scale.ToString("0.#") + ")");

            return new LeaderRay
            {
                OriginX = p.ArcX,
                OriginY = p.ArcY,
                DirX = p.DirX,
                DirY = p.DirY,
                Length = p.DistanceModel + p.FaceLongMm * LeaderCheckLengthFactor
            };
        }

        /// <summary>
        /// Odcinek w przestrzeni widoku - linia wymiarowa albo inny obiekt,
        /// którego opis ma nie zasłaniać.
        /// </summary>
        private class Segment
        {
            public double X1, Y1, X2, Y2;
        }

        /// <summary>
        /// Opis traktowany jako okrąg: środek + promień równy połowie
        /// przekątnej jego obrysu. Upraszcza całą matematykę do odległości
        /// punkt-odcinek, a dla małych bloków tekstu jest wystarczająco
        /// dokładne.
        /// </summary>
        private class MarkDisc
        {
            public Mark Mark;
            public double X, Y, Radius;
        }

        /// <summary>
        /// DELIKATNIE odsuwa te opisy (Mark), które leżą na linii odniesienia
        /// wymiaru R - ale tylko w miejsce, które jest REALNIE WOLNE.
        ///
        /// Wcześniejsza wersja przesuwała opis prostopadle do leadera w tę
        /// stronę, w której już był, i nie sprawdzała, co jest w miejscu
        /// docelowym - potrafiła więc wepchnąć opis prosto w linię wymiarową.
        /// Teraz zbieramy z rysunku przeszkody (linie wymiarowe, inne opisy,
        /// pozostałe leadery) i wybieramy spośród kilku kandydatów ten, który
        /// daje wymagany prześwit od wszystkiego.
        ///
        /// Kandydaci to obie strony prostopadłej × kilka wielokrotności
        /// brakującej różnicy. Ocena jest czystą matematyką na danych już
        /// wczytanych z API - żadnych dodatkowych zapytań do Tekli, więc
        /// nadal jest to jedno przejście i efekt jest natychmiastowy.
        /// </summary>
        private static void NudgeMarksOffLeaders(
            List<Mark> marks, List<LeaderRay> rays, List<Segment> obstacles, Action<string> log)
        {
            if (rays.Count == 0)
            {
                return;
            }

            // Opisy jako okręgi - potrzebne i jako obiekty do przesuwania, i
            // jako wzajemne przeszkody.
            var discs = new List<MarkDisc>();
            foreach (var mark in marks)
            {
                try
                {
                    var box = mark.GetAxisAlignedBoundingBox();
                    double w = Math.Abs(box.MaxPoint.X - box.MinPoint.X);
                    double h = Math.Abs(box.MaxPoint.Y - box.MinPoint.Y);
                    if (w < 1e-6 && h < 1e-6)
                    {
                        continue;   // opis bez geometrii - nie ma co przesuwać
                    }

                    discs.Add(new MarkDisc
                    {
                        Mark = mark,
                        X = (box.MinPoint.X + box.MaxPoint.X) / 2.0,
                        Y = (box.MinPoint.Y + box.MaxPoint.Y) / 2.0,
                        Radius = 0.5 * Math.Sqrt(w * w + h * h)
                    });
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden opis przy odsuwaniu – błąd: " + ex.Message);
                }
            }

            int moved = 0;

            foreach (var disc in discs)
            {
                try
                {
                    // Który leader jest naruszony i jak bardzo.
                    LeaderRay worstRay = null;
                    double worstDeficit = 0;

                    foreach (var ray in rays)
                    {
                        double lateral = LateralDistanceToRay(disc.X, disc.Y, ray);
                        if (double.IsNaN(lateral))
                        {
                            continue;   // opis poza odcinkiem leadera
                        }

                        double deficit = disc.Radius + MarkClearanceMm - lateral;
                        if (deficit > worstDeficit)
                        {
                            worstDeficit = deficit;
                            worstRay = ray;
                        }
                    }

                    if (worstRay == null)
                    {
                        continue;   // nic nie koliduje
                    }

                    // Kandydaci: obie strony prostopadłej do naruszonego
                    // leadera, kilka wielokrotności brakującej różnicy.
                    double perpX = -worstRay.DirY;
                    double perpY = worstRay.DirX;

                    double bestScore = double.NegativeInfinity;
                    double bestDx = 0, bestDy = 0;
                    bool found = false;

                    foreach (double side in new[] { 1.0, -1.0 })
                    {
                        foreach (double factor in new[] { 1.0, 1.6, 2.4, 3.5 })
                        {
                            double dist = worstDeficit * factor;
                            double dx = perpX * side * dist;
                            double dy = perpY * side * dist;

                            double clearance = MinClearance(
                                disc.X + dx, disc.Y + dy, disc.Radius,
                                disc, discs, rays, obstacles);

                            // Wolimy najmniejsze przesunięcie, które daje
                            // wymagany prześwit. Jeśli żadne nie daje -
                            // bierzemy to z największym prześwitem.
                            if (clearance >= MarkClearanceMm)
                            {
                                if (!found)
                                {
                                    found = true;
                                    bestDx = dx;
                                    bestDy = dy;
                                    bestScore = clearance;
                                }
                                break;      // ta strona załatwiona najmniejszym krokiem
                            }

                            if (!found && clearance > bestScore)
                            {
                                bestScore = clearance;
                                bestDx = dx;
                                bestDy = dy;
                            }
                        }

                        if (found)
                        {
                            break;
                        }
                    }

                    if (Math.Abs(bestDx) < 1e-9 && Math.Abs(bestDy) < 1e-9)
                    {
                        continue;
                    }

                    var attrs = disc.Mark.Attributes;
                    attrs.PlacingAttributes = new PlacingAttributes(
                        true,
                        attrs.PlacingAttributes.PlacingDistance,
                        attrs.PlacingAttributes.PlacingQuarter);
                    disc.Mark.Attributes = attrs;

                    var p = disc.Mark.InsertionPoint;
                    disc.Mark.InsertionPoint = new Tekla.Structures.Geometry3d.Point(
                        p.X + bestDx, p.Y + bestDy, p.Z);

                    if (disc.Mark.Modify())
                    {
                        moved++;
                        double shift = Math.Sqrt(bestDx * bestDx + bestDy * bestDy);
                        log("  Opis odsunięty o " + shift.ToString("0") + "mm"
                            + (found ? "" : " (nie udało się uzyskać pełnego prześwitu)")
                            + " - prześwit " + bestScore.ToString("0") + "mm.");

                        // Zaktualizuj pozycję w liście, żeby kolejne opisy
                        // widziały go tam, gdzie faktycznie jest.
                        disc.X += bestDx;
                        disc.Y += bestDy;
                    }
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden opis przy odsuwaniu – błąd: " + ex.Message);
                }
            }

            if (moved == 0)
            {
                log("  Żaden opis nie kolidował z wymiarami R.");
            }
        }

        /// <summary>
        /// Odchyłka punktu od półprostej leadera, albo NaN gdy punkt leży poza
        /// jej sprawdzanym odcinkiem (za łukiem albo dalej niż leader).
        /// </summary>
        private static double LateralDistanceToRay(double x, double y, LeaderRay ray)
        {
            double vx = x - ray.OriginX;
            double vy = y - ray.OriginY;
            double along = vx * ray.DirX + vy * ray.DirY;

            if (along < 0 || along > ray.Length)
            {
                return double.NaN;
            }

            double latX = vx - ray.DirX * along;
            double latY = vy - ray.DirY * along;
            return Math.Sqrt(latX * latX + latY * latY);
        }

        /// <summary>
        /// Najmniejszy prześwit między opisem postawionym w (x,y) a
        /// czymkolwiek na rysunku: liniami wymiarowymi, pozostałymi opisami i
        /// liniami odniesienia wymiarów R. Wartość ujemna = nachodzi.
        /// </summary>
        private static double MinClearance(
            double x, double y, double radius,
            MarkDisc self, List<MarkDisc> allMarks, List<LeaderRay> rays, List<Segment> obstacles)
        {
            double min = double.PositiveInfinity;

            foreach (var seg in obstacles)
            {
                double d = PointSegmentDistance(x, y, seg.X1, seg.Y1, seg.X2, seg.Y2) - radius;
                if (d < min) min = d;
            }

            foreach (var other in allMarks)
            {
                if (ReferenceEquals(other, self))
                {
                    continue;
                }
                double dx = x - other.X, dy = y - other.Y;
                double d = Math.Sqrt(dx * dx + dy * dy) - radius - other.Radius;
                if (d < min) min = d;
            }

            foreach (var ray in rays)
            {
                double lateral = LateralDistanceToRay(x, y, ray);
                if (double.IsNaN(lateral))
                {
                    continue;
                }
                double d = lateral - radius;
                if (d < min) min = d;
            }

            return min;
        }

        /// <summary>
        /// Czy półprosta leadera przecina którykolwiek z odcinków (linii
        /// wymiarowych)? Używane, żeby nie wpuścić tekstu wymiaru R w miejsce,
        /// do którego droga prowadzi przez inny wymiar.
        /// </summary>
        private static bool CrossesAnySegment(LeaderRay ray, List<Segment> segments)
        {
            double ex = ray.OriginX + ray.DirX * ray.Length;
            double ey = ray.OriginY + ray.DirY * ray.Length;

            foreach (var seg in segments)
            {
                if (SegmentsIntersect(
                        ray.OriginX, ray.OriginY, ex, ey,
                        seg.X1, seg.Y1, seg.X2, seg.Y2))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Jak daleko w kierunku (dirX,dirY) od punktu na łuku sięgają linie
        /// wymiarowe - w jednostkach modelu. Liczone jako największy rzut ich
        /// końców na ten kierunek, z pominięciem linii leżących poza
        /// korytarzem o zadanej połowie szerokości (te nie dotyczą tego
        /// wymiaru).
        ///
        /// Dzięki temu tekst na zewnątrz ląduje tuż ZA opisem elementu, a nie
        /// w odległości wynikającej z rozmiaru blachy - to dwie różne rzeczy.
        /// Zwraca 0, gdy w tym kierunku nie ma żadnej linii wymiarowej.
        /// </summary>
        private static double OutermostDimensionReach(
            double originX, double originY, double dirX, double dirY,
            double corridorHalfWidth, List<Segment> segments)
        {
            double maxAlong = 0;

            foreach (var seg in segments)
            {
                for (int end = 0; end < 2; end++)
                {
                    double px = end == 0 ? seg.X1 : seg.X2;
                    double py = end == 0 ? seg.Y1 : seg.Y2;

                    double vx = px - originX;
                    double vy = py - originY;

                    double along = vx * dirX + vy * dirY;
                    if (along <= 0)
                    {
                        continue;   // za łukiem, nie w tym kierunku
                    }

                    double latX = vx - dirX * along;
                    double latY = vy - dirY * along;
                    if (Math.Sqrt(latX * latX + latY * latY) > corridorHalfWidth)
                    {
                        continue;   // poza korytarzem
                    }

                    if (along > maxAlong)
                    {
                        maxAlong = along;
                    }
                }
            }

            return maxAlong;
        }

        /// <summary>
        /// Czy dwa odcinki się przecinają - klasyczny test przez znaki
        /// iloczynów wektorowych. Przypadki zdegenerowane (współliniowość,
        /// styk końcami) celowo traktowane jako BRAK przecięcia: styk nie
        /// przeszkadza, a nadwrażliwość odrzucałaby dobre pozycje.
        /// </summary>
        private static bool SegmentsIntersect(
            double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy)
        {
            double d1 = Cross(cx, cy, dx, dy, ax, ay);
            double d2 = Cross(cx, cy, dx, dy, bx, by);
            double d3 = Cross(ax, ay, bx, by, cx, cy);
            double d4 = Cross(ax, ay, bx, by, dx, dy);

            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
                && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        /// <summary>Iloczyn wektorowy (p2-p1) x (p-p1) - znak mówi, po której stronie prostej leży p.</summary>
        private static double Cross(
            double x1, double y1, double x2, double y2, double px, double py)
        {
            return (x2 - x1) * (py - y1) - (y2 - y1) * (px - x1);
        }

        /// <summary>
        /// Najkrótsza odległość punktu od ODCINKA (nie od prostej) - rzut jest
        /// przycinany do [0,1], więc dla punktów „za końcem" zwraca odległość
        /// od tego końca.
        /// </summary>
        private static double PointSegmentDistance(
            double px, double py, double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double lenSq = dx * dx + dy * dy;

            if (lenSq < 1e-12)
            {
                return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
            }

            double t = ((px - x1) * dx + (py - y1) * dy) / lenSq;
            t = Math.Max(0, Math.Min(1, t));

            double cx = x1 + t * dx, cy = y1 + t * dy;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }

        /// <summary>
        /// Zbiera linie wymiarowe z widoku jako odcinki w przestrzeni widoku -
        /// to one są główną przeszkodą, na którą nie wolno wepchnąć opisu.
        ///
        /// `StraightDimension.StartPoint/EndPoint` to punkty leżące NA CZĘŚCI
        /// (potwierdzone: dla blachy 175 x 168 wychodziły punkty typu (0,-64),
        /// (174.7,-84)), a sama linia wymiarowa jest odsunięta od nich
        /// prostopadle o `Distance` ZESTAWU. Stronę odsunięcia wybieramy tak,
        /// żeby linia wypadła DALEJ od środka części - to zwykła konwencja
        /// rysunkowa.
        ///
        /// Uwaga: bierzemy `Distance` z ZESTAWU, nie z pojedynczych wymiarów
        /// wewnątrz łańcucha - tam ta wartość znaczy coś innego (na blachy
        /// 175mm dawała 196mm, czyli bliżej długości mierzonego odcinka).
        /// </summary>
        private static List<Segment> CollectDimensionLines(ViewBase view, Action<string> log)
        {
            var segments = new List<Segment>();
            var raw = new List<(double x1, double y1, double x2, double y2, double dist)>();
            double sumX = 0, sumY = 0;
            int n = 0;

            try
            {
                DrawingObjectEnumerator sets = view.GetAllObjects(typeof(StraightDimensionSet));
                while (sets.MoveNext())
                {
                    if (!(sets.Current is StraightDimensionSet set))
                    {
                        continue;
                    }

                    double setDistance = Math.Abs(set.Distance);

                    DrawingObjectEnumerator inner = set.GetObjects();
                    while (inner.MoveNext())
                    {
                        if (!(inner.Current is StraightDimension sd))
                        {
                            continue;
                        }

                        var a = sd.StartPoint;
                        var b = sd.EndPoint;
                        raw.Add((a.X, a.Y, b.X, b.Y, setDistance));
                        sumX += a.X + b.X;
                        sumY += a.Y + b.Y;
                        n += 2;
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się odczytać linii wymiarowych: " + ex.Message);
                return segments;
            }

            if (n == 0)
            {
                return segments;
            }

            // Środek części przybliżony punktami pomiarowymi - one leżą na
            // części, więc ich średnia jest wystarczająco blisko środka.
            double centerX = sumX / n, centerY = sumY / n;

            foreach (var r in raw)
            {
                double dx = r.x2 - r.x1, dy = r.y2 - r.y1;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9)
                {
                    continue;
                }

                double perpX = -dy / len, perpY = dx / len;

                // Wybierz stronę oddalającą się od środka części.
                double midX = (r.x1 + r.x2) / 2.0, midY = (r.y1 + r.y2) / 2.0;
                if ((midX - centerX) * perpX + (midY - centerY) * perpY < 0)
                {
                    perpX = -perpX;
                    perpY = -perpY;
                }

                segments.Add(new Segment
                {
                    X1 = r.x1 + perpX * r.dist,
                    Y1 = r.y1 + perpY * r.dist,
                    X2 = r.x2 + perpX * r.dist,
                    Y2 = r.y2 + perpY * r.dist
                });
            }

            return segments;
        }

        /// <summary>
        /// Odległość w płaszczyźnie XY - Z celowo pomijane, bo pracujemy w
        /// płaskim układzie widoku rysunku.
        /// </summary>
        private static double Distance2D(Tekla.Structures.Geometry3d.Point a, Tekla.Structures.Geometry3d.Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Czy dana operacja boole'owska to OTWÓR, a nie ścięcie/wcięcie?
        ///
        /// `GetBooleans()` zwraca wszystkie operacje boole'owskie, więc bez tego
        /// filtra ścięty narożnik blachy liczył się jako otwór i tekst wymiaru
        /// szedł na zewnątrz, choć w środku części było pusto.
        ///
        /// Kryterium: wycięcie (nie dodanie materiału ani przygotowanie spoiny)
        /// częścią o profilu OKRĄGŁYM. Profile odczytane z rzeczywistego modelu
        /// (5532 operacje boole'owskie) rozkładają się tak:
        ///
        ///   okrągłe  -> D22.00, D24, D35.70, D48, D48.30      (pręt okrągły)
        ///               RD18, RD20, RD22, RD26, RD60          (pręt okrągły)
        ///               O33.7*3.2                             (rura)
        ///               RO35.7*3.2, RO48.3*3.6, RO406.4*16    (rura)
        ///   nieokrągłe -> BL... (blacha, zdecydowana większość),
        ///                 PL3.0, PLT80
        ///
        /// Stąd wzorzec: prefiks D / RD / RO / O, po którym NATYCHMIAST idzie
        /// cyfra - taka jest konwencja nazw profili w Tekli. Wymóg cyfry chroni
        /// przed przypadkowym trafieniem w nazwę własną zaczynającą się od tych
        /// liter.
        /// </summary>
        private static bool IsRoundHoleCut(Tekla.Structures.Model.ModelObject boolean)
        {
            try
            {
                if (!(boolean is Tekla.Structures.Model.BooleanPart bp))
                {
                    return false;
                }

                if (bp.Type != Tekla.Structures.Model.BooleanPart.BooleanTypeEnum.BOOLEAN_CUT)
                {
                    return false;   // dodanie materiału albo przygotowanie spoiny
                }

                string profile = bp.OperativePart?.Profile?.ProfileString;
                if (string.IsNullOrEmpty(profile))
                {
                    return false;
                }

                return RoundProfilePattern.IsMatch(profile.Trim());
            }
            catch
            {
                // Nie da się ustalić - bezpieczniej NIE liczyć jako otworu, bo
                // fałszywy otwór wypycha wymiar na zewnątrz bez potrzeby.
                return false;
            }
        }

        private static readonly System.Text.RegularExpressions.Regex RoundProfilePattern =
            new System.Text.RegularExpressions.Regex(
                @"^(RD|RO|D|O)\d",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Ustala, czy zaokrąglenia na konturze części są WYPUKŁE (zwykły
        /// zaokrąglony narożnik) czy WKLĘSŁE (wcięcie), i zapisuje wynik pod
        /// kluczem promienia w facts.ShapeByRadius.
        ///
        /// PO CO TO JEST. Znak `Distance` decyduje, po której stronie łuku
        /// wyląduje tekst, a "na zewnątrz części" to dla łuku wypukłego i
        /// wklęsłego DWIE PRZECIWNE strony:
        ///
        ///   WYPUKŁY narożnik              WKLĘSŁE wcięcie
        ///   (środek w materiale)          (środek w pustce)
        ///
        ///        tekst                          material
        ///          ^                        ####|####
        ///         /                         ####|####
        ///     ___/                          ###/
        ///    |  .środek                     __/  .środek
        ///    |#######                            |
        ///    |#######                            v tekst
        ///
        /// Dla wypukłego kierunek środek->łuk wychodzi z części, dla wklęsłego
        /// wchodzi w nią. Bez tego rozróżnienia tekst wymiaru wklęsłego
        /// przelatuje przez całą część - a na rysunku z kilkoma widokami
        /// potrafi wylądować na sąsiednim widoku (sprawdzone na [31339]).
        ///
        /// JAK TO LICZYMY. Kontur rzutujemy na jego własną płaszczyznę
        /// (odrzucamy oś o najmniejszym rozrzucie - to grubość), wyznaczamy
        /// orientację wielokąta wzorem sznurowkowym, a potem w każdym
        /// zaokrąglonym wierzchołku bierzemy znak iloczynu wektorowego
        /// krawędzi wchodzącej i wychodzącej. Znak zgodny z orientacją =
        /// wierzchołek wypukły, przeciwny = wklęsły.
        ///
        /// DLACZEGO PO PROMIENIU. API nie mówi, do którego wierzchołka konturu
        /// należy dany wymiar R - RadiusDimension zna tylko trzy punkty łuku.
        /// Promień jest więc jedynym łącznikiem. Gdy ten sam promień występuje
        /// i jako wypukły, i jako wklęsły, zapisujemy 0 i wymiar jest
        /// traktowany jak wypukły (zachowanie sprzed tej zmiany).
        ///
        /// Zgodność sprawdzona na rysunku [31339]: test wskazał wklęsłe R5 i
        /// wypukłe R20, dokładnie tak, jak człowiek ustawił znaki ręcznie
        /// (R5 -> Distance dodatni, R20 -> ujemny).
        /// </summary>
        private static void CollectRoundingShapes(
            Tekla.Structures.Model.Part modelPart, PartFacts facts)
        {
            try
            {
                if (!(modelPart is Tekla.Structures.Model.ContourPlate plate))
                {
                    return;   // tylko blachy konturowe mają kontur z fazami
                }

                var xs = new List<double[]>();
                var chamfers = new List<Tekla.Structures.Model.Chamfer>();
                foreach (var o in plate.Contour.ContourPoints)
                {
                    if (o is Tekla.Structures.Model.ContourPoint cp)
                    {
                        xs.Add(new[] { cp.X, cp.Y, cp.Z });
                        chamfers.Add(cp.Chamfer);
                    }
                }

                int n = xs.Count;
                if (n < 3)
                {
                    return;
                }

                // Płaszczyzna konturu: odrzuć oś o najmniejszym rozrzucie.
                int drop = 0;
                double worst = double.MaxValue;
                for (int axis = 0; axis < 3; axis++)
                {
                    double min = double.MaxValue, max = double.MinValue;
                    foreach (var p in xs)
                    {
                        if (p[axis] < min) min = p[axis];
                        if (p[axis] > max) max = p[axis];
                    }
                    if (max - min < worst)
                    {
                        worst = max - min;
                        drop = axis;
                    }
                }
                int ia = drop == 0 ? 1 : 0;
                int ib = drop == 2 ? 1 : 2;

                // Orientacja wielokąta (wzór sznurowkowy).
                double area2 = 0;
                for (int i = 0; i < n; i++)
                {
                    var p = xs[i];
                    var q = xs[(i + 1) % n];
                    area2 += p[ia] * q[ib] - q[ia] * p[ib];
                }
                if (Math.Abs(area2) < 1e-6)
                {
                    return;   // zdegenerowany kontur
                }
                double winding = Math.Sign(area2);

                for (int i = 0; i < n; i++)
                {
                    var ch = chamfers[i];
                    if (ch == null)
                    {
                        continue;
                    }

                    bool rounded =
                        ch.Type == Tekla.Structures.Model.Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING
                        || ch.Type == Tekla.Structures.Model.Chamfer.ChamferTypeEnum.CHAMFER_ARC
                        || ch.Type == Tekla.Structures.Model.Chamfer.ChamferTypeEnum.CHAMFER_ARC_POINT;
                    if (!rounded)
                    {
                        continue;
                    }

                    double radius = Math.Max(ch.X, ch.Y);
                    if (radius <= 0.01)
                    {
                        continue;
                    }

                    var prev = xs[(i - 1 + n) % n];
                    var cur = xs[i];
                    var next = xs[(i + 1) % n];
                    double v1a = cur[ia] - prev[ia], v1b = cur[ib] - prev[ib];
                    double v2a = next[ia] - cur[ia], v2b = next[ib] - cur[ib];
                    double cross = v1a * v2b - v1b * v2a;
                    if (Math.Abs(cross) < 1e-6)
                    {
                        continue;   // wierzchołek współliniowy - nic nie wnosi
                    }

                    int shape = Math.Sign(cross) == winding ? 1 : -1;
                    int key = (int)Math.Round(radius * 10.0);

                    if (facts.ShapeByRadius.TryGetValue(key, out int known))
                    {
                        if (known != shape)
                        {
                            facts.ShapeByRadius[key] = 0;   // sprzeczne
                        }
                    }
                    else
                    {
                        facts.ShapeByRadius[key] = shape;
                    }
                }
            }
            catch
            {
                // Brak danych o wypukłości = zachowanie jak dotąd. Nigdy nie
                // przerywamy z tego powodu rozstawiania wymiarów.
            }
        }

        /// <summary>
        /// Fakty o części, do której należy wymiar, wzięte WPROST Z MODELU:
        /// wymiary PŁASZCZYZNY blachy (bez grubości) oraz liczba otworów. To
        /// one decydują, czy tekst wymiaru może zostać w środku części.
        ///
        /// Droga: obiekt rysunkowy Part w tym samym widoku -> jego
        /// ModelIdentifier -> Model.SelectModelObject -> bryła i śruby/otwory.
        /// Rysunkowy Part sam nie ma żadnych danych geometrycznych, dlatego
        /// trzeba zejść do modelu.
        ///
        /// Z trzech wymiarów bryły ODRZUCAMY NAJMNIEJSZY - to grubość blachy,
        /// która nic nie mówi o tym, ile miejsca jest na rysunku. Zostają dwa
        /// wymiary widocznej płaszczyzny; ten mniejszy z nich ogranicza, czy
        /// tekst zmieści się w obrysie.
        ///
        /// Za otwory liczymy śruby (GetBolts - stąd biorą się opisy typu
        /// "1*Ø13") oraz te wycięcia, które faktycznie są otworami - patrz
        /// IsRoundHoleCut. `TotalCutCount` trzyma wszystkie wycięcia i służy
        /// tylko do logu, żeby było widać, ile odrzucono.
        /// </summary>
        private static PartFacts GetPartFacts(RadiusDimension rd, Action<string> log)
        {
            var facts = new PartFacts();

            try
            {
                ViewBase view = rd.GetView();
                if (view == null)
                {
                    return facts;
                }

                var model = new Tekla.Structures.Model.Model();
                if (!model.GetConnectionStatus())
                {
                    return facts;
                }

                DrawingObjectEnumerator parts = view.GetAllObjects(typeof(Part));
                while (parts.MoveNext())
                {
                    if (!(parts.Current is Part drawingPart))
                    {
                        continue;
                    }

                    var modelObject = model.SelectModelObject(drawingPart.ModelIdentifier);
                    if (!(modelObject is Tekla.Structures.Model.Part modelPart))
                    {
                        continue;
                    }

                    facts.Valid = true;

                    var solid = modelPart.GetSolid();
                    if (solid != null)
                    {
                        double dx = Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X);
                        double dy = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
                        double dz = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);

                        // Odrzuć najmniejszy wymiar (grubość) - zostają dwa
                        // wymiary widocznej płaszczyzny.
                        double smallest = Math.Min(dx, Math.Min(dy, dz));
                        double largest = Math.Max(dx, Math.Max(dy, dz));
                        double middle = dx + dy + dz - smallest - largest;

                        facts.FaceLongMm = Math.Max(facts.FaceLongMm, largest);
                        facts.FaceShortMm = Math.Max(facts.FaceShortMm, middle);
                    }

                    var bolts = modelPart.GetBolts();
                    while (bolts.MoveNext())
                    {
                        facts.BoltCount++;
                    }

                    var booleans = modelPart.GetBooleans();
                    while (booleans.MoveNext())
                    {
                        facts.TotalCutCount++;

                        if (IsRoundHoleCut(booleans.Current))
                        {
                            facts.RoundCutCount++;
                        }
                    }

                    CollectRoundingShapes(modelPart, facts);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się odczytać danych części z modelu: " + ex.Message);
                return new PartFacts();
            }

            if (facts.FaceShortMm <= 0)
            {
                facts.Valid = false;
            }

            return facts;
        }

        /// <summary>
        /// Wymiary widocznej płaszczyzny części (bez grubości) i liczba
        /// otworów - podstawa decyzji "wewnątrz czy na zewnątrz".
        /// </summary>
        private class PartFacts
        {
            public double FaceLongMm;
            public double FaceShortMm;
            public int BoltCount;

            /// Wycięcia uznane za OTWORY (okrągła część operacyjna).
            public int RoundCutCount;

            /// Wszystkie wycięcia - tylko do logu, żeby było widać, ile
            /// odrzucono jako ścięcia/wcięcia.
            public int TotalCutCount;

            public bool Valid;

            /// <summary>
            /// Wypukłość zaokrągleń konturu, po PROMIENIU (w dziesiątych
            /// milimetra, żeby klucz był całkowity). Wartości: +1 wszystkie
            /// wypukłe, -1 wszystkie wklęsłe, 0 sprzeczne.
            ///
            /// Promień jest jedynym pewnym łącznikiem między wymiarem R na
            /// rysunku a wierzchołkiem konturu w modelu - API nie mówi, do
            /// którego narożnika należy dany wymiar.
            /// </summary>
            public readonly Dictionary<int, int> ShapeByRadius = new Dictionary<int, int>();

            public int HoleCount => BoltCount + RoundCutCount;

            /// <summary>
            /// Czy łuk o tym promieniu jest WKLĘSŁY (wcięcie), a nie
            /// zaokrąglonym narożnikiem? Gdy nie wiadomo albo gdy ten sam
            /// promień występuje w obu postaciach - zwraca false, czyli
            /// zachowanie jak dotąd.
            /// </summary>
            public bool IsConcaveRadius(double radiusMm)
            {
                int key = (int)Math.Round(radiusMm * 10.0);
                foreach (int k in new[] { key, key - 1, key + 1 })
                {
                    if (ShapeByRadius.TryGetValue(k, out int shape))
                    {
                        return shape < 0;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Środek okręgu przechodzącego przez trzy punkty (circumcenter).
        /// Stąd bierzemy środek i promień łuku wymiaru - sprawdzone na żywym
        /// rysunku: dla wymiaru opisanego jako R20 wychodzi dokładnie 20,000.
        /// Rzuca wyjątkiem dla punktów współliniowych (wyznacznik ~0).
        /// </summary>
        private static Tekla.Structures.Geometry3d.Point CircumCenter(
            Tekla.Structures.Geometry3d.Point p1, Tekla.Structures.Geometry3d.Point p2, Tekla.Structures.Geometry3d.Point p3)
        {
            double ax = p1.X, ay = p1.Y;
            double bx = p2.X, by = p2.Y;
            double cx = p3.X, cy = p3.Y;

            double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(d) < 1e-9)
            {
                throw new InvalidOperationException("Punkty łuku są współliniowe.");
            }

            double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
            double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;

            return new Tekla.Structures.Geometry3d.Point(ux, uy, 0);
        }

        /// <summary>
        /// Ustawia wymiar R na tryb Fixed z podanym (podpisanym) Distance w
        /// jednostkach MODELU - w trybie Fixed to WŁAŚNIE znak Distance
        /// kontroluje, po której stronie linii bazowej ląduje tekst (w
        /// przeciwieństwie do trybu Free, gdzie strona jest ustalona na
        /// sztywno per wymiar i niezależna od żadnego atrybutu - patrz
        /// komentarz w TryPlaceSmart).
        /// </summary>
        private static void SetFixedDistance(RadiusDimension rd, Drawing activeDrawing, double modelDistance)
        {
            var attrs = rd.Attributes;
            attrs.Placing = new DimensionSetBaseAttributes.DimensionPlacingAttributes(
                DimensionSetBaseAttributes.Placings.Fixed,
                new PlacingDirectionAttributes(true, true),
                new PlacingDistanceAttributes(2.0, Math.Abs(modelDistance)));
            rd.Attributes = attrs;
            rd.Distance = modelDistance;
            rd.Modify();
            activeDrawing.CommitChanges();
        }

        /// <summary>
        /// Rozstawia JEDEN wymiar R WBUDOWANYM w Teklę silnikiem
        /// auto-rozstawiania (Attributes.Placing = Placings.Free) - ten sam
        /// mechanizm co przy StraightDimensionSet. Używane jako wariant
        /// AWARYJNY (gdy wizyjne wymuszanie strony, TryPlaceSmart, jest
        /// niedostępne albo zawiedzie) - Free unika kolizji z innymi
        /// elementami, ale (potwierdzone empirycznie) czasem ląduje w
        /// środku konturu części, bo wybrana strona jest ustalona na sztywno
        /// per wymiar i żaden atrybut (Direction Positive/Negative) tego nie
        /// zmienia.
        ///
        /// WAŻNE - dwuetapowość jest konieczna: samo ustawienie Placing=Free
        /// z nowymi parametrami wyszukiwania NIE wymusza ponownego
        /// przeliczenia, jeśli wymiar był już wcześniej w trybie Free
        /// (Tekla zdaje się cache'ować wynik). Trzeba najpierw przełączyć na
        /// Fixed z dowolnym Distance, zapisać, i DOPIERO wtedy przełączyć
        /// na Free ze świeżymi parametrami - potwierdzone empirycznie na
        /// żywym rysunku.
        ///
        /// WAŻNE - jednostki: Distance jest w mm NA PAPIERZE, mimo że
        /// ArcPoint1/2/3 w tej samej klasie są w jednostkach MODELU. Liczymy
        /// w modelu i dzielimy przez skalę widoku przed zapisem. Pomyłka tutaj
        /// daje błąd równy skali rysunku (na 1:5 pięciokrotny) i objawia się
        /// tekstem lądującym po drugiej stronie części.
        /// </summary>
        private bool PlaceUsingFreeMode(RadiusDimension rd, Drawing activeDrawing, double scale, Action<string> log)
        {
            var resetAttrs = rd.Attributes;
            resetAttrs.Placing = new DimensionSetBaseAttributes.DimensionPlacingAttributes(
                DimensionSetBaseAttributes.Placings.Fixed,
                new PlacingDirectionAttributes(true, true),
                new PlacingDistanceAttributes(2.0, ResetDistanceMm / scale));
            rd.Attributes = resetAttrs;
            rd.Distance = ResetDistanceMm / scale;
            rd.Modify();
            activeDrawing.CommitChanges();
            Thread.Sleep(300);

            var freeAttrs = rd.Attributes;
            freeAttrs.Placing = new DimensionSetBaseAttributes.DimensionPlacingAttributes(
                DimensionSetBaseAttributes.Placings.Free,
                new PlacingDirectionAttributes(true, true),
                new PlacingDistanceAttributes(SearchMarginMm / scale, MinimalDistanceMm / scale, MaximalDistanceMm / scale));
            rd.Attributes = freeAttrs;

            bool modifyResult = rd.Modify();
            activeDrawing.CommitChanges();
            Thread.Sleep(300);

            if (modifyResult)
            {
                log("  Wymiar R rozstawiony trybem awaryjnym (Tekla Placing=Free, margines " + SearchMarginMm + "mm, zakres " + MinimalDistanceMm + "-" + MaximalDistanceMm + "mm na papierze).");
            }

            return modifyResult;
        }

        /// <summary>
        /// Dociąga opisy (Mark, np. "1*Ø13") bliżej tego, co opisują -
        /// domyślnie mają MaximalDistance=0 (bez limitu), więc Tekla czasem
        /// wyrzuca je bardzo daleko szukając wolnego miejsca. Ten sam
        /// dwuetapowy wzorzec reset-Fixed-potem-Free co dla wymiarów R, bo
        /// MarkBase.Attributes.PlacingAttributes dzieli tę samą logikę
        /// wyszukiwania (PlacingDistanceAttributes) co RadiusDimension.
        /// </summary>
        private void TightenMarks(
            List<Mark> marks, Dictionary<ViewBase, double> scaleCache, Action<string> log)
        {
            int tightened = 0;

            foreach (var mark in marks)
            {
                try
                {
                    double scale = GetViewScale(mark, scaleCache, log);

                    // Tryb "auto" (IsFixed=false) z ciasnym zakresem
                    // wyszukiwania, zamiast domyślnego "bez limitu", który
                    // potrafił wyrzucić opis bardzo daleko od tego, co opisuje.
                    var attrs = mark.Attributes;
                    attrs.PlacingAttributes = new PlacingAttributes(
                        false,
                        new PlacingDistanceAttributes(
                            MarkSearchMarginMm / scale,
                            MarkMinimalDistanceMm / scale,
                            MarkMaximalDistanceMm / scale),
                        attrs.PlacingAttributes.PlacingQuarter);
                    mark.Attributes = attrs;

                    if (mark.Modify())
                    {
                        tightened++;
                    }
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden opis – błąd: " + ex.Message);
                }
            }

            log("  Dociągnięto " + tightened + " opis(ów) bliżej elementu (zakres "
                + MarkMinimalDistanceMm + "-" + MarkMaximalDistanceMm + "mm na papierze).");
        }

        /// <summary>
        /// Zwraca skalę widoku (np. 5.0 dla rysunku szczegółowego "5:1"), w
        /// którym leży dany obiekt (wymiar R, opis...) - liczone raz na
        /// widok i zapamiętane w cache. Bezpieczny fallback = 1.0 (stare,
        /// "1mm = 1mm" zachowanie), jeśli nie da się odczytać widoku/skali.
        /// </summary>
        private static double GetViewScale(DrawingObject rd, Dictionary<ViewBase, double> cache, Action<string> log)
        {
            ViewBase viewBase;
            try
            {
                viewBase = rd.GetView();
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się pobrać widoku wymiaru R - użyto skali 1.0. Błąd: " + ex.Message);
                return 1.0;
            }

            if (viewBase == null)
            {
                return 1.0;
            }

            if (cache.TryGetValue(viewBase, out double cachedScale))
            {
                return cachedScale;
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

            cache[viewBase] = scale;
            return scale;
        }

        /// <summary>
        /// Krótki opis tego, co jest teraz otwarte w Tekli - wyłącznie do
        /// pokazania w podpisie pod przyciskiem. Przycisk "Przesuń" jest
        /// zawsze klikalny, więc nic tu nie decyduje o jego stanie.
        /// </summary>
        public string GetCurrentDrawingDescription()
        {
            try
            {
                var drawingHandler = new DrawingHandler();
                if (!drawingHandler.GetConnectionStatus())
                {
                    return "Brak połączenia z Teklą - uruchom Teklę i otwórz rysunek.";
                }

                Drawing activeDrawing = drawingHandler.GetActiveDrawing();
                if (activeDrawing == null)
                {
                    return "Brak otwartego rysunku - otwórz rysunek w edytorze rysunków Tekli.";
                }

                return "Rysunek: " + activeDrawing.Name;
            }
            catch (Exception ex)
            {
                return "Nie udało się odczytać stanu Tekli: " + ex.Message;
            }
        }
    }
}

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

        // --- Rozstawianie wymiarów R: WYŁĄCZNIE ze współrzędnych z API
        // (ArcPoint1/2/3, bryła części, StraightDimensionSet.Distance).
        // Wszystko w jednostkach MODELU, tak jak Distance. Żadnych zrzutów
        // ekranu ani analizy pikseli - patrz TryPlaceByGeometry(). ---

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

        // Dodatkowy, BEZWZGLĘDNY próg na krótszy wymiar płaszczyzny.
        //
        // Konieczny, bo API nie podaje pozycji tekstu wymiaru: przy
        // Distance=23 tekst odjechał ~100mm od łuku. Na blachy 66 x 181 bez
        // otworów oba wymiary R przeleciały więc na skos przez materiał i
        // wylądowały POD blachą, na sobie i na wymiarze długości. Do środka
        // wchodzimy tylko wtedy, gdy blacha jest szeroka na tyle, że takie
        // przestrzelenie nie wyprowadza tekstu poza obrys.
        private const double InsideMinShortFaceMm = 120.0;

        // Jak głęboko w część wchodzi tekst, gdy wolno mu tam zostać -
        // ułamek KRÓTSZEGO wymiaru płaszczyzny, żeby tekst nie wyszedł
        // obrysem po przeciwnej stronie.
        private const double InsideFraction = 0.35;

        // Jak daleko na zewnątrz od łuku ląduje tekst - ułamek największego
        // wymiaru części. Wartość dobrana z rzeczywistych rysunków: na blachy
        // 175mm daje ~18mm, czyli tuż za opisem elementu.
        private const double OutsideFraction = 0.10;

        // Jak długi odcinek linii odniesienia sprawdzamy pod kątem kolizji z
        // opisami - jako wielokrotność rozmiaru części, DODANA do Distance.
        //
        // Musi być hojne, bo API nie podaje pozycji tekstu wymiaru, a tekst
        // ląduje znacznie dalej, niż sugeruje samo Distance: na blachy 175mm
        // przy Distance=17 opis "1*Ø13" stykający się z tekstem wymiaru leżał
        // ~140mm wzdłuż promienia. Zbyt krótki zasięg powodował, że program w
        // ogóle nie widział kolizji. Przeszacowanie jest tu tanie: opis leżący
        // dalej i tak zostanie odsunięty tylko w bok i tylko o brakującą
        // różnicę, więc zostaje przy swoim otworze.
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
        /// WAŻNE - jednostki: PlacingDistanceAttributes (SearchMargin,
        /// MinimalDistance, MaximalDistance) są w jednostkach MODELU, tak
        /// samo jak zwykłe Distance - trzeba dzielić przez skalę widoku,
        /// inaczej (potwierdzone empirycznie) wymiar wyleci daleko poza
        /// widok przy widoku w powiększonej skali.
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

            // Promienie linii odniesienia wymiarów R - potrzebne w ostatnim
            // etapie, żeby wiedzieć, czego opisy mają nie zasłaniać.
            var leaderRays = new List<LeaderRay>();

            foreach (var rd in radiusDimensions)
            {
                try
                {
                    double scale = GetViewScale(rd, scaleCache, log);

                    var ray = TryPlaceByGeometry(rd, log);
                    if (ray == null)
                    {
                        // Geometria się nie udała (np. zdegenerowany łuk) -
                        // zostaje wbudowany silnik Tekli jako wariant
                        // awaryjny, żeby program zawsze coś zrobił.
                        if (!PlaceUsingFreeMode(rd, activeDrawing, scale, log))
                        {
                            log("  Jeden wymiar R nie został zmodyfikowany (Modify() zwróciło false).");
                            continue;
                        }
                    }
                    else
                    {
                        leaderRays.Add(ray);
                    }

                    result.MovedCount++;
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden wymiar R – błąd: " + ex.Message);
                }
            }

            activeDrawing.CommitChanges();

            NudgeMarksOffLeaders(marks, leaderRays, log);
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
        /// Ustawia JEDEN wymiar R, licząc wszystko WYŁĄCZNIE ze współrzędnych
        /// z API Tekli - żadnych zrzutów ekranu, żadnego szukania po kroku.
        /// Jedno wyliczenie, jedno Modify().
        ///
        /// Dane wejściowe (wszystko w jednostkach MODELU, ta sama przestrzeń
        /// co Distance): ArcPoint1/2/3 -> środek i promień łuku
        /// (circumcenter), bryła części z modelu -> rozmiar i liczba otworów.
        ///
        /// Zasada rozstawiania (ustalona z użytkownikiem):
        /// - część większa niż MinPartSizeForInsideMm i BEZ otworów -> tekst
        ///   zostaje wewnątrz części (jest tam pusto),
        /// - część z otworem albo mniejsza -> tekst na zewnątrz.
        ///
        /// ZNAK Distance: ujemny = NA ZEWNĄTRZ (od środka łuku), dodatni = do
        /// wnętrza części. Ustalone empirycznie - RadiusDimension nie
        /// udostępnia swojej pozycji, więc konwencji nie da się odczytać z
        /// API; kilkanaście niezależnych pomiarów na trzech różnych rysunkach
        /// dało za każdym razem ten sam wynik. Jeśli kiedyś okaże się
        /// odwrotnie, wystarczy odwrócić OutwardSign.
        ///
        /// Zwraca półprostą leadera (do odsuwania opisów) albo null, gdy
        /// geometrii nie da się policzyć - wtedy wywołujący spada do
        /// PlaceUsingFreeMode.
        /// </summary>
        private LeaderRay TryPlaceByGeometry(RadiusDimension rd, Action<string> log)
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

            // Tekst może zostać WEWNĄTRZ tylko gdy część nie ma żadnych
            // otworów I w płaszczyźnie blachy jest wokół łuku realne miejsce.
            //
            // Miarą "jest miejsce" jest KRÓTSZY wymiar płaszczyzny w stosunku
            // do promienia, a nie stały próg na największym wymiarze. Ten
            // pierwotny wariant (>300mm na max wymiarze) wyrzucał na zewnątrz
            // blachę 65,5 x 180,8 bez otworów, w której tekst spokojnie się
            // mieścił - 180,8 nie przechodziło progu, choć miejsca było dość.
            bool roomInside = facts.FaceShortMm >= radius * InsideRoomRadiusFactor
                && facts.FaceShortMm >= InsideMinShortFaceMm;
            bool insideAllowed = facts.HoleCount == 0 && roomInside;

            log("  Część: płaszczyzna " + facts.FaceShortMm.ToString("0") + " x " + facts.FaceLongMm.ToString("0")
                + "mm, śruby: " + facts.BoltCount + ", wycięcia: " + facts.BooleanCount
                + ", promień łuku " + radius.ToString("0") + "mm.");

            double distance;
            if (insideAllowed)
            {
                // Do wnętrza dużej, pustej części - na tyle głęboko, żeby
                // tekst nie siedział na samej krawędzi.
                distance = -OutwardSign * facts.FaceShortMm * InsideFraction;
                log("  -> tekst WEWNĄTRZ części (Distance=" + distance.ToString("0") + ").");
            }
            else
            {
                // Na zewnątrz - odległość jako ułamek rozmiaru części.
                //
                // Dlaczego NIE liczymy tego z StraightDimensionSet.Distance,
                // choć taka wartość jest w API: nie jest w tej samej skali co
                // RadiusDimension.Distance. Na blachy 175mm łańcuchy raportują
                // Distance 25-120, a wstawienie 120 wyrzuciło tekst poza
                // arkusz, podczas gdy ~15-25 ląduje tuż za opisem. Ponieważ
                // RadiusDimension NIE udostępnia swojej pozycji przez API,
                // nie ma czym tego przeliczyć. Rozmiar części z modelu jest
                // stabilną i wystarczającą podstawą.
                distance = OutwardSign * facts.FaceLongMm * OutsideFraction;
                log("  -> tekst NA ZEWNĄTRZ (Distance=" + distance.ToString("0") + ").");
            }

            var attrs = rd.Attributes;
            attrs.Placing = new DimensionSetBaseAttributes.DimensionPlacingAttributes(
                DimensionSetBaseAttributes.Placings.Fixed,
                new PlacingDirectionAttributes(true, true),
                new PlacingDistanceAttributes(2.0, Math.Abs(distance)));
            rd.Attributes = attrs;
            rd.Distance = distance;
            rd.Modify();

            // Leader biegnie od łuku w stronę tekstu. Dokładnej długości nie
            // znamy (API nie podaje pozycji tekstu), więc bierzemy z zapasem -
            // chodzi tylko o to, żeby opisy nie leżały na tej linii.
            double sign = insideAllowed ? -1.0 : 1.0;
            return new LeaderRay
            {
                OriginX = center.X + dirX * radius * sign,
                OriginY = center.Y + dirY * radius * sign,
                DirX = dirX * sign,
                DirY = dirY * sign,
                Length = Math.Abs(distance) + facts.FaceLongMm * LeaderCheckLengthFactor
            };
        }

        /// <summary>
        /// DELIKATNIE odsuwa w bok te opisy (Mark), które leżą na linii
        /// odniesienia któregoś wymiaru R - żeby nie zasłaniały wymiaru.
        ///
        /// Liczone analitycznie, jednym przejściem: rzut środka opisu na
        /// półprostą leadera daje odległość wzdłuż linii i odchyłkę w bok.
        /// Jeśli odchyłka jest mniejsza niż potrzebny prześwit (połowa
        /// przekątnej opisu + margines), opis przesuwamy PROSTOPADLE do
        /// leadera dokładnie o brakującą różnicę - ani o milimetr więcej.
        /// Żadnego szukania po kroku, żadnych prób.
        ///
        /// Przesuwany opis musi mieć wyłączone automatyczne rozstawianie
        /// (IsFixed=true), inaczej Tekla przy najbliższej okazji przeliczyłaby
        /// jego pozycję i przesunięcie by zniknęło.
        /// </summary>
        private static void NudgeMarksOffLeaders(List<Mark> marks, List<LeaderRay> rays, Action<string> log)
        {
            if (rays.Count == 0)
            {
                return;
            }

            int moved = 0;

            foreach (var mark in marks)
            {
                try
                {
                    var box = mark.GetAxisAlignedBoundingBox();
                    double width = Math.Abs(box.MaxPoint.X - box.MinPoint.X);
                    double height = Math.Abs(box.MaxPoint.Y - box.MinPoint.Y);
                    if (width < 1e-6 && height < 1e-6)
                    {
                        // Opis bez geometrii (np. pusty) - nie ma co przesuwać.
                        continue;
                    }

                    double cx = (box.MinPoint.X + box.MaxPoint.X) / 2.0;
                    double cy = (box.MinPoint.Y + box.MaxPoint.Y) / 2.0;

                    // Prześwit liczony z przekątnej, bo leader może biec pod
                    // dowolnym kątem - wtedy "w poprzek" opisu jest właśnie
                    // przekątna, nie sama wysokość.
                    double needed = 0.5 * Math.Sqrt(width * width + height * height) + MarkClearanceMm;

                    double bestPushX = 0, bestPushY = 0, worstDeficit = 0;

                    foreach (var ray in rays)
                    {
                        double vx = cx - ray.OriginX;
                        double vy = cy - ray.OriginY;

                        double along = vx * ray.DirX + vy * ray.DirY;
                        if (along < 0 || along > ray.Length)
                        {
                            // Opis jest za łukiem albo dalej niż leader -
                            // nie koliduje.
                            continue;
                        }

                        double latX = vx - ray.DirX * along;
                        double latY = vy - ray.DirY * along;
                        double lateral = Math.Sqrt(latX * latX + latY * latY);

                        double deficit = needed - lateral;
                        if (deficit <= 0)
                        {
                            continue;
                        }

                        // Kierunek odsunięcia: prostopadle do leadera, w tę
                        // stronę, w której opis już jest. Gdy leży dokładnie
                        // na linii, wybieramy dowolną prostopadłą.
                        double pushX, pushY;
                        if (lateral > 1e-6)
                        {
                            pushX = latX / lateral;
                            pushY = latY / lateral;
                        }
                        else
                        {
                            pushX = -ray.DirY;
                            pushY = ray.DirX;
                        }

                        if (deficit > worstDeficit)
                        {
                            worstDeficit = deficit;
                            bestPushX = pushX * deficit;
                            bestPushY = pushY * deficit;
                        }
                    }

                    if (worstDeficit <= 0)
                    {
                        continue;
                    }

                    var attrs = mark.Attributes;
                    attrs.PlacingAttributes = new PlacingAttributes(
                        true,
                        attrs.PlacingAttributes.PlacingDistance,
                        attrs.PlacingAttributes.PlacingQuarter);
                    mark.Attributes = attrs;

                    var p = mark.InsertionPoint;
                    mark.InsertionPoint = new Tekla.Structures.Geometry3d.Point(
                        p.X + bestPushX, p.Y + bestPushY, p.Z);

                    if (mark.Modify())
                    {
                        moved++;
                        log("  Opis odsunięty o " + worstDeficit.ToString("0") + "mm w bok, żeby nie zasłaniał wymiaru R.");
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

        private static double Distance2D(Tekla.Structures.Geometry3d.Point a, Tekla.Structures.Geometry3d.Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
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
        /// "1*Ø13") oraz wycięcia (GetBooleans). UWAGA: GetBooleans zwraca
        /// wszystkie operacje boole'owskie, więc np. ścięty narożnik też się
        /// tu policzy jako "otwór" - dlatego jedno i drugie logujemy osobno.
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
                        facts.BooleanCount++;
                    }
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
            public int BooleanCount;
            public bool Valid;

            public int HoleCount => BoltCount + BooleanCount;
        }

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
        /// WAŻNE - jednostki: PlacingDistanceAttributes (SearchMargin,
        /// MinimalDistance, MaximalDistance) są w jednostkach MODELU, tak
        /// samo jak zwykłe Distance - trzeba dzielić przez skalę widoku,
        /// inaczej (potwierdzone empirycznie) wymiar wyleci daleko poza
        /// widok przy widoku w powiększonej skali.
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

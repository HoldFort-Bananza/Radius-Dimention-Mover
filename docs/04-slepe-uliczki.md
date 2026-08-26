[← Spis treści](index.md)

# 4. Ślepe uliczki

**Przeczytaj to przed zmianami w rozstawianiu.** Każde podejście niżej zostało
zaimplementowane i przetestowane na żywych rysunkach. Wszystkie zawiodły, z
konkretnych powodów. Bez tej listy łatwo powtórzyć kilka dni pracy.

---

## 1. `Placing=Free` jako główny mechanizm

**Pomysł:** Tekla ma wbudowany silnik auto-rozstawiania wymiarów
(`Placing = Free`), ten sam co dla łańcuchów wymiarów prostych. Wystarczy go
włączyć.

**Dlaczego nie działa jako ścieżka główna:** `Free` unika kolizji, ale
**stronę/kąt wybiera sam**, „na sztywno" per wymiar, i nie da się tego
narzucić. Skutek: część wymiarów ląduje **wewnątrz obrysu** części.

**Co sprawdzono, żeby to zmienić** (trzy niezależne testy, każdy weryfikowany
zrzutem odpornym na zasłonięcia):

- `PlacingDirectionAttributes(positive: true, negative: true)` — domyślne
- `PlacingDirectionAttributes(true, false)` — tylko dodatni
- `PlacingDirectionAttributes(false, true)` — tylko ujemny
- reset do `Fixed` z **ujemnym** `Distance` przed przełączeniem na `Free`

**Wynik: wszystkie cztery dały bajtowo identyczny rezultat.** `Direction` nie
ma żadnego wpływu na wybraną stronę.

Sprawdzono też, czy istnieje odpowiednik `PlacingQuarter` (jak w `Mark`) —
`DimensionSetBaseAttributes.DimensionPlacingAttributes` przyjmuje tylko
`Placings` / `Direction` / `Distance`. Nie ma czegoś takiego.

**Status:** zostało jako **wariant awaryjny** (`PlaceUsingFreeMode`), gdy
geometrii nie da się policzyć.

---

## 2. Wykrywanie obrysu części po kolorze pikseli

**Pomysł:** zrobić zrzut okna Tekli i znaleźć obrys części jako bounding box
„prawie białych" pikseli, żeby wiedzieć, co jest „w środku".

**Dlaczego nie działa:** linie wymiarowe, strzałki i pomocnicze linie widoku są
rysowane **tym samym prawie-białym kolorem** co krawędzie części. Nie da się
ich odróżnić samą analizą koloru.

**Zmierzone wyniki:**

- zasięg globalny (całe okno): wykryty „obrys" ≈ **2559 × 1397 px** na oknie
  2576 × 1416 — czyli praktycznie cały rysunek. Warunek „poza obrysem" nigdy
  nie był spełniony, nawet przy 290 mm odsunięcia.
- zasięg lokalny (kwadrat wokół wymiaru): nadal nasycał się do niemal całego
  obszaru szukania (np. 839 × 741 px z maks. 840 × 840).

**Status:** porzucone. Kod usunięty.

---

## 3. Środek ciężkości różnicy zrzutów jako pozycja tekstu

**Pomysł:** zrobić zrzut przed i po zmianie `Distance`, policzyć środek
ciężkości zmienionych pikseli — to powinna być nowa pozycja tekstu.

**Dlaczego nie działa:** przy przesunięciu zmieniają się **dwa** miejsca —
stara i nowa pozycja wymiaru. Środek ciężkości wypada **pomiędzy nimi**, więc
nie wskazuje tekstu.

**Objaw:** sprawdzanie zajętości zwracało czyste `0,00` w miejscach, gdzie
tekst wyraźnie leżał na otworze.

**Status:** porzucone. Zastąpione bezpośrednim pomiarem nakładania
(piksel w piksel, `GetOverlapWithExisting`) — który działał lepiej, ale i tak
został usunięty razem z całą wizją (punkt 6).

---

## 4. Umieszczanie tekstu wewnątrz części przez ujemny `Distance`

**Pomysł:** skoro ujemny `Distance` przenosi tekst na drugą stronę, to na dużej
pustej blachy trafi w jej wnętrze.

**Dlaczego jest kruche:** ujemny `Distance` **nie** znaczy „bliżej, w głąb
materiału" — przenosi tekst na **przeciwną stronę środka okręgu**. Przy
zaokrągleniu narożnika daje to z definicji długą linię odniesienia przez pół
części.

**Zmierzone:**

- blacha 538 × 141 bez otworów: wyszło **dobrze** — tekst w pustym wnętrzu.
- blacha 175 × 168 z otworami: oba teksty przeleciały na skos przez część i
  wylądowały przy otworach, nachodząc na siebie.
- blacha 66 × 181 bez otworów, `Distance = 23`: oba teksty przeleciały na skos
  i wylądowały **pod** blachą, jeden na drugim i na wymiarze długości. Tekst
  odjechał od łuku **~100 mm**, mimo `Distance = 23`.

**Status:** dozwolone, ale **tylko dla szerokich blach** —
`InsideMinShortFaceMm = 120`. Obniżenie do 60 zostało przetestowane i daje
wynik z ostatniego punktu wyżej.

---

## 5. `StraightDimensionSet.Distance` jako odniesienie „jak daleko są linie"

**Pomysł:** ustawić tekst wymiaru R za najdalszym łańcuchem wymiarowym —
odczytać `Distance` wszystkich łańcuchów i wziąć maksimum plus offset.

**Dlaczego nie działa:** ta wartość **nie jest w tej samej skali** co
`RadiusDimension.Distance`. Na blachy 175 mm łańcuchy raportują 25–120;
wstawienie 120 wyrzuciło tekst **poza arkusz**, podczas gdy ~15–25 ląduje tuż
za opisem.

Dodatkowo `Distance` na pojedynczym `StraightDimension` wewnątrz łańcucha
znaczy jeszcze coś innego (na tej samej blachy dawało 196 mm).

Bez odczytu pozycji tekstu — którego API nie udostępnia — nie ma czym tego
przeliczyć.

**Status:** porzucone. Odległość liczona z rozmiaru części.

---

## 6. Sterowanie pozycją na podstawie obrazu (cała rodzina podejść)

Przez dłuższy czas program: robił zrzuty okna Tekli (`PrintWindow` z
`PW_RENDERFULLCONTENT` — odporny na zasłonięcia, w przeciwieństwie do
`Graphics.CopyFromScreen`), przesuwał wymiar krok po kroku i po każdym kroku
mierzył nakładanie pikseli, wybierając najbliższą wolną pozycję.

**To działało**, ale zostało **usunięte na wyraźne polecenie**: program ma
opierać się wyłącznie na danych z API.

Efekt uboczny usunięcia: zamiast ~16 rund ze zrzutami na wymiar jest jedno
`Modify()`. Program działa natychmiast.

**Jeśli ktoś kiedyś będzie chciał wrócić do wizji**, dwie rzeczy warte
zapamiętania:

- `Graphics.CopyFromScreen` łapie to, co jest **faktycznie** na ekranie, więc
  inne okno na wierzchu psuje pomiar. `PrintWindow` z flagą
  `PW_RENDERFULLCONTENT = 2` jest odporny — potwierdzone kontrolowanym testem z
  celowym zasłonięciem okna.
- Pomarańczowa ramka arkusza to RGB(254,101,0), zielone linie widoku
  RGB(0,159,0). Antyaliasing rozmywa te wartości, więc filtrowanie po kolorze
  z wąskimi progami zawodzi.

---

## 7. Bounding box widoku jako miara rozmiaru części

**Pomysł:** `ViewBase.GetAxisAlignedBoundingBox()` × skala daje rozmiar części
w jednostkach modelu; można z tego liczyć ułamkami odległości.

**Dlaczego nie działa:** bbox widoku obejmuje **wszystko**, co jest w widoku —
w tym wymiary. Kiedy wymiary zostaną wyrzucone daleko, bbox rośnie, więc
**każde kolejne uruchomienie liczy większe odległości niż poprzednie**. Pętla
sprzężenia zwrotnego.

**Zmierzone:** na blachy 175 mm rozmiar odniesienia doszedł do **2600 mm** po
kilku uruchomieniach, a odległości do 636 mm (tekst poza arkuszem).

**Status:** zastąpione rozmiarem bryły z modelu, który jest stały i niezależny
od tego, gdzie leżą wymiary.

---

## 8. Blokada przycisku po przesunięciu

**Pomysł:** po udanym przesunięciu zablokować przycisk, żeby przypadkowe
podwójne kliknięcie nie powtarzało operacji; odblokować przy zmianie rysunku
albo ręcznej korekcie.

**Dlaczego przeszkadzało:** Tekla **nie zgłasza zdarzenia przy Ctrl+Z**. Po
cofnięciu zmian przycisk zostawał szary i nie było jak przesunąć ponownie.

**Status:** usunięte razem z całym śledzeniem stanu per rysunek. Przycisk jest
zawsze aktywny, ponowne przesunięcie jest nieszkodliwe.

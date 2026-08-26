[← Spis treści](index.md)

# 8. Znane ograniczenia

Uczciwa lista tego, czego program **nie** potrafi, i dlaczego. Wszystko poniżej
wynika z jednego korzenia.

---

## Korzeń wszystkiego: nie da się odczytać pozycji tekstu wymiaru

`RadiusDimension` nie udostępnia przez API swojego położenia na rysunku:

- `ArcPoint1/2/3` opisują **łuk**, nie tekst — identyczne przy `Distance = 3`
  i `Distance = 88,7`
- brak jakiegokolwiek akcesora bounding boxa
- `GetRelatedObjects()` → 0 obiektów
- `GetDimensionSet()` rzuca wyjątkiem dla samodzielnego wymiaru R

Program ustawia `Distance` i **nie ma jak sprawdzić wyniku**. To nie jest
kwestia niedopracowania — takiej metody po prostu nie ma w API.

### Konsekwencja 1: `Distance` to nie odległość tekstu

Zmierzone na żywych rysunkach:

| Rysunek | `Distance` | Gdzie faktycznie wylądował tekst |
|---|---|---|
| blacha 66 × 181 | 23 | ~100 mm od łuku |
| blacha 175 × 168 | 17 | opis stykający się z tekstem leżał ~140 mm wzdłuż promienia |

Zależności nie udało się ustalić — bez odczytu pozycji nie ma czego
kalibrować. Dlatego wszystkie liczby dotyczące pozycji tekstu są
oszacowaniami dobranymi na rzeczywistych rysunkach, a nie wyliczeniami.

### Konsekwencja 2: znak „na zewnątrz" jest stałą w kodzie

`OutwardSign = -1.0` ustalono empirycznie (kilkanaście pomiarów na trzech
rysunkach, zawsze ten sam wynik). Gdyby na jakiejś wersji Tekli konwencja była
odwrotna, wszystkie wymiary poszłyby w złą stronę i trzeba by zmienić jedną
liczbę.

### Konsekwencja 3: zasięg sprawdzania kolizji jest przeszacowany

`LeaderCheckLengthFactor = 1,5` × rozmiar części. Wynika wprost z tego, że nie
wiemy, gdzie kończy się linia odniesienia. Skutek uboczny: teoretycznie może
odsunąć opis leżący dalej, niż sięga rzeczywisty wymiar. W praktyce
nieszkodliwe — odsunięcie jest prostopadłe i tylko o brakującą różnicę.

---

## Umieszczanie wewnątrz części — tylko szerokie blachy

Wymiar zostaje w środku dopiero przy krótszym wymiarze płaszczyzny **≥ 120 mm**
(`InsideMinShortFaceMm`), i tylko gdy część nie ma otworów.

**Dlaczego nie na wąskich:** ujemny `Distance` nie oznacza „bliżej, w głąb
materiału" — przenosi tekst na przeciwną stronę środka okręgu. Przy
zaokrągleniu narożnika daje to długą linię odniesienia przez część. Na blachy
66 × 181 (bez otworów, `Distance = 23`) oba wymiary R przeleciały na skos i
wylądowały **pod** blachą, jeden na drugim i na wymiarze długości.

To zostało sprawdzone: obniżenie progu do 60 mm odtwarza dokładnie ten wynik.

**Co by to naprawiło:** znajomość pozycji tekstu. Bez niej jedyne obejście, jakie
widzę, to zrezygnować z `Distance` i wstawiać własny obiekt tekstowy w
policzonym punkcie — ale to przestałby być wymiar R Tekli, tylko podpis obok
niego. Nie zostało zaimplementowane.

---

## Zliczanie otworów bywa zbyt czułe

`HoleCount = GetBolts() + GetBooleans()`, a `GetBooleans()` zwraca **wszystkie**
operacje boole'owskie — nie tylko otwory. Ścięty narożnik czy dopasowanie też
się policzy, i część zostanie potraktowana jako „ma otwór" → wymiar pójdzie na
zewnątrz, choć w środku było pusto.

Na testowanych rysunkach `GetBooleans()` zwracało 0, więc problem się nie
ujawnił. Program loguje śruby i wycięcia **osobno**, żeby dało się to od razu
rozpoznać w logu.

**Możliwa poprawa:** rozpoznawać, czy wycięcie jest okrągłe (przez profil
`OperativePart`), i liczyć tylko takie. Nie zrobione — brak przypadku testowego.

---

## Założenia geometryczne

- **Widok nieobrócony.** Kierunek na zewnątrz liczony jest w płaszczyźnie XY
  współrzędnych widoku. Przy obróconym widoku może wyjść inaczej — nie
  testowano.
- **Zaokrąglenie wypukłe.** Przyjęto, że środek okręgu leży po stronie
  materiału (typowy narożnik blachy). Dla **wklęsłego** wcięcia jest odwrotnie
  i kierunek „na zewnątrz" wyjdzie odwrócony. Nie testowano.
- **Jedna część na rysunek.** `GetPartFacts` bierze maksimum z części w widoku.
  Na rysunku zestawczym z wieloma częściami wynik będzie mieszanką.

---

## Czego program świadomie nie robi

| Nie robi | Dlaczego |
|---|---|
| Nie czyta ekranu, nie analizuje pikseli | Wymaganie: tylko API. Wcześniejsze podejście oparte na obrazie działało, ale zostało usunięte. |
| Nie ma własnego „Cofnij" | **Ctrl+Z** w Tekli. Wcześniejsza blokada przycisku psuła pracę, bo Tekla nie zgłasza zdarzenia przy Ctrl+Z. |
| Nie iteruje, nie sprawdza wyniku | Nie ma czym sprawdzić (patrz wyżej). Wszystko liczone jednym rachunkiem. |
| Nie ma pól konfiguracyjnych w UI | Wymaganie: jedno kliknięcie, bez podawania zmiennych. Regulacja przez stałe w kodzie. |
| Nie zapamiętuje stanu między kliknięciami | Ponowne przesunięcie jest nieszkodliwe — liczy od nowa z tych samych danych. |

---

## Otwarte pytania dla następnej osoby

1. Czy istnieje sposób odczytania pozycji tekstu wymiaru R? Sprawdzone i
   wyczerpane: `ArcPoint*`, bounding boxy, `GetRelatedObjects`,
   `GetDimensionSet`. Może pomogłoby `DrawingObjectSelector` albo `Picker`,
   albo eksport rysunku do DWG i odczyt z niego.
2. Czy da się odtworzyć obrys części w przestrzeni widoku z
   `StraightDimension.StartPoint/EndPoint`? Te punkty leżą **na części** i są
   w tych samych współrzędnych co `ArcPoint*`. Pozwoliłoby to sprawdzać, czy
   punkt jest w obrysie, bez czytania ekranu.
3. Jak zachowuje się to wszystko przy widoku obróconym i przy wcięciu
   wklęsłym?

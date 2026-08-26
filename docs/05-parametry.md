[← Spis treści](index.md)

# 5. Parametry

Wszystkie stałe siedzą na górze `RadiusDimensionService.cs`. Nie ma pliku
konfiguracyjnego ani pól w UI — świadomie, bo program ma działać „na jedno
kliknięcie, bez podawania zmiennych".

---

## Rozstawianie wymiarów R

| Stała | Wartość | Co robi |
|---|---|---|
| `OutwardSign` | `-1.0` | Znak `Distance` odpowiadający kierunkowi **na zewnątrz**. |
| `InsideRoomRadiusFactor` | `3.0` | Wewnątrz tylko gdy krótszy wymiar płaszczyzny ≥ 3× promień łuku. |
| `InsideMinShortFaceMm` | `60.0` | Wewnątrz tylko gdy krótszy wymiar płaszczyzny ≥ 60 mm. |
| `InsideFraction` | `0.35` | Głębokość wejścia w część = ułamek **krótszego** wymiaru płaszczyzny. |
| `OutsideFraction` | `0.10` | Odległość na zewnątrz = ułamek **dłuższego** wymiaru płaszczyzny. |

### `OutwardSign` — kiedy zmienić

Jeśli **wszystkie** wymiary R zaczną lądować po złej stronie (do środka, gdy
powinny na zewnątrz), zmień `-1.0` na `1.0`. Nie ma sensu kombinować inaczej —
konwencja jest globalna.

Wartość ustalona empirycznie: API nie pozwala jej wyliczyć, ale kilkanaście
niezależnych pomiarów na trzech rysunkach dało za każdym razem ten sam wynik.

### `InsideMinShortFaceMm` — najbardziej wrażliwa stała

Decyduje, jak wąska blacha jeszcze dostaje wymiar w środku.

| Wartość | Skutek |
|---|---|
| `60.0` (**obecnie**) | Wąskie blachy też idą do środka. Wybrane świadomie — patrz ostrzeżenie niżej. |
| `120.0` | Wewnątrz tylko szerokie blachy (np. 538 × 141). Wariant bezpieczny. |
| bardzo duża (np. `9999`) | Praktycznie wyłącza umieszczanie wewnątrz. |

⚠️ **Znany skutek ustawienia 60 mm.** Na blachy **66 × 181 bez otworów** oba
wymiary R przelatują na skos przez materiał i lądują **pod** blachą, jeden na
drugim i na wymiarze długości. Sprawdzone na żywym rysunku.

Przyczyna nie leży w tej stałej: `Distance` nie przekłada się wprost na
odległość tekstu — przy `Distance = 23` tekst odjechał od łuku ~100 mm, a
blacha ma tylko 66 mm wysokości, więc kierunek „do środka" z górnego narożnika
wychodzi dolną krawędzią. Wartość `120.0` to próg, przy którym takie
przestrzelenie zostaje w obrysie.

Jeśli zobaczysz teksty wymiarów pod częścią albo nachodzące na siebie —
podnieś tę stałą do `120.0`. Szersze wyjaśnienie:
[Ograniczenia](08-ograniczenia.md#umieszczanie-wewnątrz-części).

### `InsideFraction` / `OutsideFraction`

Ułamki **rzeczywistego rozmiaru części** z modelu, nie mm. Dzięki temu ta sama
wartość działa na detalu 5:1 i na blachy 1:5.

- `OutsideFraction = 0.10` na blachy 175 mm daje ~18 mm — tuż za opisem
  elementu. To wartość dobrana na rzeczywistych rysunkach i zaakceptowana.
- `InsideFraction = 0.35` na blachy 141 mm szerokości daje ~49 mm.

---

## Odsuwanie opisów od linii odniesienia

| Stała | Wartość | Co robi |
|---|---|---|
| `MarkClearanceMm` | `12.0` | Minimalny prześwit **ponad** połowę przekątnej opisu (mm w modelu). |
| `LeaderCheckLengthFactor` | `1.5` | Długość sprawdzanego odcinka linii odniesienia = `\|Distance\| + 1,5 × rozmiar części`. |

### `MarkClearanceMm`

Jedyna stała do regulacji „jak daleko odsuwać opisy". Przy 12 mm na
rzeczywistym rysunku wyszły korekty 9 mm i 19 mm.

- za mało → opisy dalej stykają się z tekstem wymiaru
- za dużo → opisy odjeżdżają niepotrzebnie od swoich otworów

### `LeaderCheckLengthFactor`

Musi być **hojne**, bo API nie podaje pozycji tekstu wymiaru. Na blachy 175 mm
przy `Distance = 17` opis stykający się z tekstem leżał ~140 mm wzdłuż
promienia — przy ciasnym oszacowaniu program w ogóle nie widział kolizji
(log mówił „żaden opis nie kolidował", a wizualnie nachodziły).

Przeszacowanie jest tanie: opis leżący dalej i tak zostanie odsunięty tylko
prostopadle i tylko o brakującą różnicę.

---

## Dociąganie opisów (etap 1)

| Stała | Wartość | Co robi |
|---|---|---|
| `MarkSearchMarginMm` | `15.0` | `SearchMargin` dla auto-rozstawiania opisów. |
| `MarkMinimalDistanceMm` | `10.0` | Minimalna odległość opisu od tego, co opisuje. |
| `MarkMaximalDistanceMm` | `60.0` | **Maksymalna** — to sedno; domyślne 0 znaczy „bez limitu". |

Te trzy są w mm **na papierze** i dzielone przez `View.Attributes.Scale` przed
wysłaniem do API (patrz [Jednostki](03-api-tekli.md#jednostki)).

---

## Wariant awaryjny (`Placing=Free`)

| Stała | Wartość | Co robi |
|---|---|---|
| `SearchMarginMm` | `30.0` | Margines szukania dla silnika Tekli. |
| `MinimalDistanceMm` | `15.0` | Dolna granica zakresu szukania. |
| `MaximalDistanceMm` | `300.0` | Górna granica zakresu szukania. |
| `ResetDistanceMm` | `4.0` | Neutralna wartość przy resecie `Fixed` → `Free`. |

Używane tylko gdy geometrii nie da się policzyć. Zmiana ich nie wpływa na
normalne działanie programu.

---

## UI

| Stała | Wartość | Co robi |
|---|---|---|
| `ConnectRetryIntervalMs` | `3000` | Jak często ponawiać próbę połączenia, **dopóki** nie ma Tekli. Po połączeniu timer jest zatrzymywany. |

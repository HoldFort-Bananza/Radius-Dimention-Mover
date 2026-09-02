# Radius Dimension Mover

Samodzielny `.exe` dla Tekla Structures 2025. Jedno kliknięcie porządkuje
wszystkie wymiary promieni (`R…`) na otwartym rysunku. Reguły ogólne dla
środowiska są w `..\CLAUDE.md`.

**Dokumentacja:** https://github.com/HoldFort-Bananza/Radius-Dimention-Mover/wiki
— zacznij od stron `2-Algorytm`, `3-API-Tekli` i **`4-Slepe-uliczki`**.
Tej ostatniej nie pomijaj: jedenaście podejść, które zostały zaimplementowane,
zmierzone na żywych rysunkach i **nie działają**. Bez niej powtórzysz kilka dni
pracy.

## Struktura

| Plik | Zawartość |
|---|---|
| `RadiusDimensionService.cs` | cała logika, zero wiedzy o UI |
| `MainForm.cs` | tylko UI, zdarzenia Tekli, sprawdzanie aktualizacji |
| `UpdateCheck.cs` | powiadomienie o nowszym release z GitHuba |
| `Program.cs` | wyłącznie punkt wejścia |
| `installer/setup.iss` | Inno Setup |

## Fakt, z którego wynika wszystko inne

**`RadiusDimension` nie udostępnia pozycji swojego tekstu.** Nie ma bounding
boxa, `ArcPoint1/2/3` opisują sam łuk i nie zmieniają się przy przesuwaniu
tekstu, `GetRelatedObjects()` zwraca pustkę. Program ustawia `Distance` i **nie
ma jak sprawdzić, gdzie tekst wylądował**. Stąd wynikają wszystkie kompromisy:
położenie musi być policzone analitycznie, jednym przejściem, bez iteracji i
bez weryfikacji po fakcie.

## Semantyka, której nie ma w dokumentacji Tekli

- **`Distance` jest w mm na papierze**, choć `ArcPoint*` w tej samej klasie są
  w jednostkach modelu. Zapis: `distanceModel / scale`.
- **Znak `Distance` decyduje o stronie łuku.** Ujemny wysyła tekst w kierunku
  środek okręgu → łuk, dodatni w przeciwnym. `PlacingDirectionAttributes`
  tego **nie** kontroluje.
- Dla zaokrąglonego narożnika „na zewnątrz" to strona ujemna, dla **wcięcia
  odwrotnie** — środek okręgu wcięcia leży w pustce, nie w materiale.
  Wypukłość ustala `CollectRoundingShapes` z konturu w modelu.
- **`punkt_na_arkuszu = view.Origin + punkt_w_widoku / skala`** — sprawdzone.
- **Obrót widoku nie psuje niczego** — punkty przychodzą już w układzie widoku.
- **Bbox widoku puchnie**, gdy obiekty odjeżdżają. Nie nadaje się na rozmiar
  części.
- **Odległości nie liczy się od `StraightDimension.StartPoint/EndPoint`** — te
  leżą na części, a linia wymiarowa jest odsunięta o `set.Distance`.

## Jak ustalać progi i stałe

**Mierz, nie zgaduj.** Najskuteczniejsza metoda w tym projekcie:

1. Operator cofa rysunek do stanu domyślnego (`Ctrl+Z`).
2. Zrzut wszystkich odczytywalnych wartości do pliku.
3. Operator ustawia wymiary **ręcznie, tak jak mają być**.
4. Drugi zrzut i różnica.

Z delty `Distance` wychodzi oczekiwana reguła wprost: znak mówi, po której
stronie łuku, wartość mówi jak daleko. Tak potwierdzono kryterium wypukłości
(4 na 4) i ustalono semantykę znaku.

⚠️ **Dopasowuj wymiary po współrzędnych łuku, nie po numerze.** Kolejność w
`GetAllObjects()` **nie jest stabilna** między uruchomieniami.

## Testowanie

Rysunek do testu musi otworzyć operator. Para regresyjna po każdej zmianie
rozstawiania:

| Rysunek | Co sprawdza |
|---|---|
| `[31202]` | blacha 100×200 ze ścięciami, próg wąskiej blachy, wyrównanie |
| `[31339]` | dwa łuki wklęsłe i dwa wypukłe, drugi widok na arkuszu |

**Skala projektu:** program ma znaczenie dla **125 rysunków** (tyle blach ma
zaokrąglenie na konturze), nie 2298. Warto to pamiętać, oceniając, ile pracy
wart jest kolejny przypadek brzegowy.

Kandydata do testu wybiera się **od rysunków do części** —
`GetModelObjectIdentifiers` działa na zamkniętym rysunku, więc pełny przelot to
sekundy. Pozycja w modelu może w ogóle nie mieć rysunku.

## Rusztowanie diagnostyczne

Przy diagnozowaniu wygodnie dodać do `Program.cs` tymczasowe przełączniki
(`--testrun`, `--snapshot`, `--inspect`) plus plik `Inspector.cs`.
**Usunąć przed commitem** — wydawany program ma mieć tylko `Main` z `MainForm`.
Po usunięciu przebudować i przywrócić `bin/` z commitu, żeby instalator pakował
czysty `.exe`.

## Wydawanie wersji

Wersję trzeba podnieść w **dwóch** plikach, trzecim miejscem jest tag release:

- `RadiusDimensionMover.csproj` → `<Version>`
- `installer/setup.iss` → `MyAppVersion`
- tag na GitHubie musi wskazywać **wydany commit**

Program porównuje swoją wersję z tagiem, więc rozjazd daje fałszywe
powiadomienie o aktualizacji. Po wydaniu skopiować `.exe`, `.exe.config` i
`.pdb` do `%LOCALAPPDATA%\Programs\RadiusDimensionMover\` — inaczej skrót z
pulpitu uruchamia starą wersję.

Token do GitHuba brać z magazynu poświadczeń gita
(`git credential fill`), trzymać w zmiennej powłoki, **nigdy** nie wpisywać do
polecenia ani nie logować, i wyczyścić po użyciu. Dłuższy opis release składać
w Pythonie i wysyłać przez `--data-binary @plik.json` — wielolinijkowy tekst
wprost w `-d` daje błąd 400.

Procedura krok po kroku: wiki, strona `6-Budowanie`.

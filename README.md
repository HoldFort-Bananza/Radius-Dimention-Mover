# Radius Dimension Mover – Tekla Structures 2025

📖 **[Pełna dokumentacja projektu](https://holdfort-bananza.github.io/Radius-Dimention-Mover/)**
— architektura, algorytm, fakty o API Tekli, ślepe uliczki, parametry,
budowanie, diagnostyka i znane ograniczenia. Jeśli przejmujesz ten projekt,
zacznij tam.

Osobna aplikacja (.exe) do szybkiego przesuwania **wszystkich wymiarów R**
na aktywnym rysunku na zewnątrz elementu, żeby tekst nie wpadał w kontur
rysunku ani w inne teksty/wymiary.

## Jak to działa

1. Aplikacja łączy się z uruchomioną Teklą przez Tekla Open API (`DrawingHandler`),
   dokładnie tak samo jak zwykłe makra Tekli – różnica jest tylko taka, że to
   osobny plik .exe, a nie skrypt uruchamiany z wnętrza Tekli.
2. Bierze aktualnie otwarty rysunek (`GetActiveDrawing()`).
3. Przechodzi po wszystkich obiektach na arkuszu i wybiera te typu `RadiusDimension`
   i `Mark` (opisy typu "1*Ø13" ze śrub/otworów, z leaderem).
4. Najpierw dociąga opisy (`Mark`) bliżej tego, co opisują - ustawia
   `Attributes.PlacingAttributes` na tryb auto (`IsFixed=false`) z ciasnym
   zakresem szukania 10-60mm na papierze, zamiast domyślnego "bez limitu",
   który potrafił wyrzucić opis bardzo daleko.
5. Dla każdego wymiaru R liczy geometrię łuku z `ArcPoint1/2/3` (środek
   okręgu i promień) - wszystko w jednostkach modelu, tej samej przestrzeni
   co `Distance`.
6. **Decyduje, czy tekst zostaje WEWNĄTRZ części, czy idzie NA ZEWNĄTRZ.**
   W środku zostaje tylko wtedy, gdy spełnione są OBA warunki:
   - część nie ma **żadnego** otworu (`GetBolts()` + `GetBooleans()` = 0),
   - **krótszy** wymiar płaszczyzny blachy (bez grubości) jest co najmniej
     60mm i co najmniej 3× promień łuku (stała `InsideMinShortFaceMm`).

   W przeciwnym razie tekst idzie na zewnątrz, w pobliże linii wymiarowych
   opisujących element.

   Wymiary bierze z bryły części (`Part.GetSolid()`) - z trzech wymiarów
   odrzuca najmniejszy, bo to grubość blachy i nic nie mówi o tym, ile jest
   miejsca na rysunku. Droga do modelu: rysunkowy `Part.ModelIdentifier` →
   `Model.SelectModelObject`.

   Dlaczego próg jest na **krótszym wymiarze płaszczyzny**, a nie na
   największym wymiarze bryły: blacha 65,5 × 180,8 bez otworów, w której
   tekst spokojnie się mieścił, była wyrzucana na zewnątrz, bo 180,8 nie
   przechodziło progu 300mm. Dlaczego dodatkowo próg **bezwzględny**:
   patrz "Znane ograniczenia" - bez niego wymiary na blachy 66 × 181
   przelatywały na skos przez materiał i lądowały POD nią.
7. Ustawia `Distance` jako ułamek rzeczywistego rozmiaru części (ze znakiem
   zależnym od tego, czy tekst ma iść na zewnątrz, czy do wnętrza). Jeśli
   geometrii nie da się policzyć (np. zdegenerowany łuk) albo nie ma danych z
   modelu, wymiar spada do wbudowanego w Teklę trybu **`Placing=Free`** jako
   bezpiecznego wariantu awaryjnego.
8. **Odsuwa delikatnie te opisy (`Mark`), które leżą na linii odniesienia
   wymiaru R** - żeby nie zasłaniały wymiaru. Rzut środka opisu na półprostą
   leadera daje odległość wzdłuż linii i odchyłkę w bok; jeśli odchyłka jest
   mniejsza niż potrzebny prześwit (połowa przekątnej opisu + 12mm), opis
   przesuwa się PROSTOPADLE do leadera dokładnie o brakującą różnicę - ani o
   milimetr więcej, więc zostaje przy swoim otworze. Przesunięty opis dostaje
   `IsFixed=true`, inaczej Tekla przeliczyłaby jego pozycję z powrotem.
9. Zapisuje zmiany (`Modify()` na każdym obiekcie + `CommitChanges()` na rysunku).

Wszystko liczone jest **analitycznie, jednym przejściem** - program nie próbuje
kolejnych pozycji "aż się uda". Trzy etapy (dociągnięcie opisów → wymiary R →
odsunięcie kolidujących opisów), każdy to jedno przejście po obiektach i jeden
`CommitChanges()`. Efekt jest natychmiastowy.

**Program działa wyłącznie na danych z Tekla Open API** - nie robi zrzutów
ekranu, nie analizuje pikseli, nie czyta okna Tekli. Wszystkie decyzje
wynikają ze współrzędnych i właściwości obiektów.

Odległości są **ułamkiem rzeczywistego rozmiaru części** (z bryły w modelu), a
nie stałymi milimetrami - `Distance` i `ArcPoint1/2/3` są w jednostkach
MODELU, więc stałe "mm" znaczyły zupełnie inną odległość na detalu 5:1 niż na
blachy 1:5. Rozmiaru **nie** bierzemy z bounding boxa widoku: ten rośnie, gdy
wymiary zostaną wyrzucone daleko, co tworzyło pętlę sprzężenia (każde kolejne
uruchomienie liczyło coraz większe odległości - na blachy 175mm doszło do
2600mm).

### Znane ograniczenia

- **`RadiusDimension` nie udostępnia swojej pozycji na rysunku.** `ArcPoint1/2/3`
  opisują sam łuk i nie zmieniają się przy przesuwaniu tekstu, nie ma
  bounding boxa, `GetRelatedObjects()` zwraca pustkę. Dlatego program nie
  może sprawdzić, gdzie tekst faktycznie wylądował - ustawia `Distance` i
  ufa Tekli.
- **Z tego wynika najważniejsze ograniczenie: `Distance` nie przekłada się
  wprost na odległość tekstu.** Na blachy 66 × 181 przy `Distance=23` tekst
  odjechał od łuku ~100mm. Dlatego umieszczanie WEWNĄTRZ jest dopuszczone
  tylko powyżej progu `InsideMinShortFaceMm` - poniżej oba wymiary R
  przelatywały na skos przez materiał i lądowały pod blachą, jeden na drugim
  i na wymiarze długości. Nie da się tego rozwiązać dokładniejszym
  liczeniem, dopóki API nie podaje pozycji tekstu.
- Z tego samego powodu **znak `Distance` (która strona to "na zewnątrz")
  jest stałą w kodzie** (`OutwardSign`), ustaloną na rzeczywistych rysunkach -
  nie da się go wyliczyć z API. Gdyby kiedyś wyszło odwrotnie, wystarczy
  zmienić tam `-1` na `+1`.
- **`StraightDimensionSet.Distance` nie jest w tej samej skali co
  `RadiusDimension.Distance`** - na blachy 175mm łańcuchy raportują 25-120,
  a wstawienie 120 wyrzuca tekst poza arkusz. Dlatego odległość liczymy z
  rozmiaru części, nie z odsunięcia istniejących łańcuchów.
- **`Placing=Free`** (wbudowany silnik Tekli) unika kolizji, ale kąt/stronę
  wybiera sam, "na sztywno" per wymiar, i żaden atrybut (`Direction`
  Positive/Negative) tego nie zmienia - sprawdzone trzema niezależnymi
  testami. Dlatego jest tylko wariantem awaryjnym.

## Bezpieczniki

- **Przycisk jest zawsze klikalny** - bez żadnej blokady. Przesunięcie drugi
  raz nic nie psuje (wymiary są po prostu rozstawiane od nowa), a wcześniejsza
  blokada powodowała problem: Tekla nie zgłasza cofnięcia przez **Ctrl+Z**,
  więc po Ctrl+Z przycisk zostawał szary i nie było jak przesunąć ponownie.
- **Cofanie**: zwykłym **Ctrl+Z** w Tekli - program nie ma własnego "Cofnij".
- **Podpis pod przyciskiem** pokazuje, jaki rysunek jest teraz otwarty -
  aktualizowany ze zdarzeń Tekli (`Tekla.Structures.Drawing.UI.Events`:
  `DrawingLoaded`, `DrawingEditorOpened`, `DrawingEditorClosed`), więc po
  przejściu na inny rysunek od razu widać zmianę.
- **Log sesji**: każda sesja programu zapisuje pełny log do pliku w
  `logs\session_<data_godzina>.log` obok pliku .exe.

## Wymagania

- Tekla Structures 2025 zainstalowana na tym samym komputerze (z ważną licencją).
- Internet podczas instalacji (instalator pobiera biblioteki Tekla Open API).

## Instalacja

1. Pobierz `RadiusDimensionMover-Setup-vX.Y.exe` z [Releases](../../releases).
2. Uruchom go i zaakceptuj pokazaną licencję (EULA Trimble/Tekla).
3. Instalator sam pobierze wymagane biblioteki Tekla Open API świeżo z
   publicznego NuGet (nuget.org) - pod Twoją własną licencją Tekli, dokładnie
   to samo co zrobiłby `dotnet restore`, tylko zautomatyzowane. Te biblioteki
   **nie są dołączone do repo/instalatora** - są własnością Trimble/Tekla i
   ich licencja zabrania redystrybucji stronom trzecim, a to repo jest
   publiczne. Instalator tylko automatyzuje ich legalne pobranie na Twój
   komputer.
4. Gotowe - skrót w Menu Start (i opcjonalnie na pulpicie).

Źródła instalatora (w pełni jawne, nic ukrytego) są w folderze `installer/`:
`setup.iss` (Inno Setup) i `fetch-dependencies.ps1` (skrypt pobierający
biblioteki).

## Uruchamianie

1. Uruchom Teklę, otwórz model, otwórz w edytorze rysunek pojedynczej części.
2. Uruchom Radius Dimension Mover (skrót z Menu Start/pulpitu).
3. Kliknij **"Przesuń wszystkie wymiary R (unikaj kolizji)"** - jeden
   przycisk, bez żadnych parametrów do wpisywania.
4. Log w oknie pokaże ile wymiarów znaleziono i ile udało się rozstawić.
5. Wróć do Tekli i sprawdź wzrokowo. Jeśli coś jest nie tak, wciśnij
   **Ctrl+Z** w Tekli.
6. Przejdź na kolejny rysunek - przycisk sam się odblokuje.

## Budowanie z kodu źródłowego (dla programistów)

1. Otwórz `RadiusDimensionMover.csproj` w Visual Studio (albo `dotnet build
   RadiusDimensionMover.csproj -c Debug -p:Platform=x64`).
2. NuGet automatycznie pobierze pakiety `Tekla.Structures*` w wersji 2025.0.0.
   Jeśli masz inną wersję Tekli, popraw wersję pakietów w `.csproj`.
3. Zbuduj projekt. Powstanie `bin\x64\Debug\net48\RadiusDimensionMover.exe`.

Żeby zbudować sam instalator: zainstaluj [Inno Setup](https://jrsoftware.org/isinfo.php),
zbuduj najpierw projekt jak wyżej, potem uruchom `ISCC.exe installer\setup.iss`.

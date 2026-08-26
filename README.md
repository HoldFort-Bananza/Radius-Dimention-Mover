# Radius Dimension Mover – Tekla Structures 2025

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
5. Dla każdego wymiaru R liczy kierunek "na zewnątrz części" **z geometrii
   łuku** (`ArcPoint1/2/3` → środek okręgu; dla wypukłego zaokrąglenia
   narożnika środek leży po stronie materiału, więc kierunek "od środka na
   zewnątrz" = kierunek "od materiału") i sprawdza jednym zrzutem ekranu,
   czy znak `+`/`-` w Tekli zgadza się z tym kierunkiem.
6. **Decyduje, czy tekst może zostać WEWNĄTRZ części, czy musi iść NA
   ZEWNĄTRZ** - na podstawie danych z modelu, nie analizy obrazu:
   - część **większa niż 300mm i bez żadnego otworu** → tekst zostaje w
     środku (jest tam pusto, rysunek jest najbardziej zwarty),
   - część **z otworem albo mniejsza niż 300mm** → tekst idzie na zewnątrz,
     w pobliże linii wymiarowych opisujących element.

   Rozmiar bierze z bryły części (`Part.GetSolid()`), a otwory z
   `GetBolts()` + `GetBooleans()` - droga: rysunkowy `Part.ModelIdentifier`
   → `Model.SelectModelObject`.
7. Jeśli tekst idzie na zewnątrz, odsuwa wymiar krok po kroku i po każdym
   kroku **mierzy na zrzucie ekranu, jaka część nowo narysowanego wymiaru
   nałożyła się na to, co już tam było** (piksel w piksel, bez zgadywania
   pozycji tekstu - patrz `WindowCapture.GetOverlapWithExisting`). Wybiera
   **najbliższą** pozycję, w której nic się nie nakłada, żeby rysunek został
   zwarty. Krawędź arkusza (pomarańczowa ramka) jest twardym limitem -
   program nigdy nie wyjdzie poza nią. Jeśli z jakiegokolwiek powodu (np.
   zdegenerowana geometria, okno Tekli nie znalezione) nie da się tego
   ustalić, wymiar spada do wbudowanego w Teklę trybu **`Placing=Free`** jako
   bezpiecznego wariantu awaryjnego.
8. Zapisuje zmiany (`Modify()` na każdym obiekcie + `CommitChanges()` na rysunku).

Odległości szukania są **ułamkiem rzeczywistego rozmiaru części** (z bryły w
modelu), a nie stałymi milimetrami - `Distance` i `ArcPoint1/2/3` są w
jednostkach MODELU, więc stałe "mm" znaczyły zupełnie inną odległość na
detalu 5:1 niż na blachy 1:5. Rozmiaru **nie** bierzemy z bounding boxa
widoku: ten rośnie, gdy wymiary zostaną wyrzucone daleko, co tworzyło pętlę
sprzężenia (każde kolejne uruchomienie liczyło coraz większe odległości - na
blachy 175mm doszło do 2600mm).

### Czego się nie da i dlaczego

- **`Placing=Free`** (wbudowany silnik Tekli) unika kolizji, ale kąt/stronę
  wybiera sam, "na sztywno" per wymiar, i żaden atrybut (`Direction`
  Positive/Negative) tego nie zmienia - sprawdzone trzema niezależnymi
  testami. Dlatego jest tylko wariantem awaryjnym.
- **Wykrywanie konturu części po kolorze pikseli** zawiodło - linie i
  strzałki wymiarowe są rysowane tym samym prawie-białym kolorem co krawędzie
  części, więc wykryty "kontur" wychodził na niemal cały rysunek.

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

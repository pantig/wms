# WMS pallet simulation

Prosta aplikacja webowa w .NET 10 symulujaca zaladunek i rozladunek magazynu palet 9 x 3.

## Model

- Magazyn ma 9 kolumn na osi X i 3 rzedy na osi Y.
- Polnocny bok jest gorna krawedzia rzutu. W wizualizacji `Y=3` lezy przy N, a `Y=1` przy S.
- Kazda pozycja przechowuje jeden stack palet.
- Maksymalna wysokosc stacka to 7 jednostek.
- Nie da sie polozyc wiekszej palety na mniejszej: `C` ma wysokosc 3, `B` 2, `A` 1.
- Dostep do pozycji jest liczony wylacznie w tej samej kolumnie od strony N. Nie mozna obchodzic blokady przez sasiednia kolumne.

## FIFO

Kazda paleta dostaje identyfikator i czas wjazdu. Stack ma priorytet FIFO liczony z wieku palet:

- najwazniejsza jest najstarsza paleta w stacku,
- dodatkowo pokazywana jest suma wieku wszystkich palet w stacku,
- rozladowac mozna stack pelny i aktualnie dostepny od strony N; stack `6/7` moze wejsc tylko jako awaryjne zdjecie blokady.

## Zaladunek

Zaladunek odbywa sie tylko od strony N. Dla otwierania nowych pozycji symulator trzyma priorytet kolumnowy:

```text
X1Y1, X1Y2, X1Y3,
X2Y1, X2Y2, X2Y3,
...
X9Y1, X9Y2, X9Y3
```

W obecnym ukladzie `Y=3` lezy przy N, a `Y=1` przy S, dlatego taka kolejnosc oznacza wypelnianie kazdej kolumny od najglebszej pozycji do strony dostepu. Jezeli trzeba otworzyc nowa pozycje, wybierana jest pierwsza legalna pozycja z tej sekwencji.

Dla istniejacych stackow dziala dodatkowy priorytet operacyjny:

- najwyzej jest `6/7 + A`, czyli dostepny stack o wysokosci 6 jest dopelniany paleta `A` do `7/7`,
- potem wybierane sa inne ruchy, ktore domykaja stack do `7/7`,
- potem uzupelniane sa dostepne niepelne stacki wedlug aging FIFO: im dluzej najstarsza paleta lezy w stacku i im wieksza jest suma wieku palet, tym wyzszy priorytet,
- dopiero na koncu otwierana jest nowa pozycja w kolejnosci kolumnowej.

Palety nie sa odrzucane. Jezeli dla nowej palety nie da sie znalezc zadnej legalnej pozycji, symulacja zatrzymuje sie w stanie blokady i zapisuje komunikat:

```text
blokada - brak miejsca na magazynie
```

W ramach tej kolejnosci nadal obowiazuja reguly dostepu od N, maksymalnej wysokosci 7 i zakazu stawiania wiekszej palety na mniejszej. Jezeli w kolumnie `X1` zajete jest `X1Y3`, to `X1Y2` i `X1Y1` sa zablokowane, nawet jesli sasiednia kolumna jest pusta.

Zeby ograniczac blokowanie niepelnych stackow, symulator nie otwiera pozycji blizej strony N, dopoki wszystkie glebsze pozycje w tej samej kolumnie nie maja co najmniej `6/7`. Innymi slowy, `X1Y2` moze zostac otwarte dopiero wtedy, gdy `X1Y1` ma wysokosc 6 albo 7; `X1Y3` dopiero wtedy, gdy `X1Y1` i `X1Y2` maja wysokosc 6 albo 7.

## Rozladunek

Rozladunek ma dwa poziomy czasu:

- cykl rozladunku domyslnie wypada co 5 minut,
- cykl moze wystartowac tylko wtedy, gdy da sie zaplanowac dokladnie 9 pelnych stackow,
- jezeli plan ma mniej niz 9 stackow, symulator probuje dolaczyc awaryjne stacki `6/7`, ale tylko takie, ktore odblokowuja pozycje za nimi w tej samej kolumnie,
- jezeli nadal plan ma mniej niz 9 stackow, cykl czeka i startuje dopiero po uzbieraniu 9,
- jezeli pelnych stackow jest wiecej, w danym cyklu zdejmowanych jest tylko 9,
- w aktywnym cyklu zdejmowany jest jeden stack co 5 sekund,
- pierwszy stack jest zdejmowany w chwili startu cyklu, a kolejne po 5 sekundach, wiec 9 stackow zajmuje 40 sekund,
- podczas aktywnego cyklu rozladunku zaladunek jest wstrzymany, bo ten sam dostep od strony N jest zarezerwowany dla rozladunku,
- stack musi miec pelna wysokosc 7 i musi byc aktualnie dostepny,
- stack `6/7` moze wejsc do planu tylko jako rozladunek awaryjny dla blokady,
- jezeli dostepnych jest kilka stackow z planu, wygrywa stack z najstarsza paleta FIFO.

## Web UI

Widok webowy pokazuje rzut kartezjanski magazynu, status dostepnosci pozycji, pelne stacki, kolejke FIFO i plan serii po 9. Kazda komorka pokazuje:

- wysokosc stacka,
- liczbe palet `A`, `B` i `C`,
- kolejnosc palet od dolu do gory,
- wizualny przekroj stacka,
- wiek najstarszej palety i sume wieku stacka.

Panel sterowania pozwala zatrzymac czas, przejsc do poprzedniego lub nastepnego kroku historii, zresetowac symulacje oraz ustawic interwal zaladunku, interwal cyklu rozladunku i czas pomiedzy stackami w aktywnym cyklu. Czasy mozna ustawic z dokladnoscia do setnych sekundy, z minimalna wartoscia `0.05 s`.

## Uruchamianie

```powershell
dotnet run
```

Domyslny adres z profilu Kestrel albo ustawiony recznie:

```powershell
dotnet run -- --urls http://localhost:5177
```

Po zbudowaniu mozna uruchomic bezposrednio DLL:

```powershell
dotnet bin\Debug\net10.0\wms.dll --urls http://localhost:5177
```

## API

- `GET /api/simulation` - aktualny snapshot symulacji.
- `POST /api/control/play` - wznowienie czasu.
- `POST /api/control/pause` - zatrzymanie czasu.
- `POST /api/control/previous` - poprzedni krok historii.
- `POST /api/control/next` - nastepny krok historii albo nowy krok symulacji.
- `POST /api/control/reset` - reset symulacji.
- `PUT /api/settings` - ustawienie czasu zaladunku, cyklu rozladunku i stacka w cyklu w sekundach.

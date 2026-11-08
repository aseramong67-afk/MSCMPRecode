# MP Recode Remastered (v0.3.0)

Мультиплеер-мод для My Summer Car 0.2.15 (MSCLoader 1.4.2).
Транспорт: **Steam P2P (лобби Steamworks)**, вход **по коду сессии** — у хоста есть код, клиент его вводит.
TCP-туннель tcp.cloudpub (v0.2) убран, его исходники в `MPRecode\legacy\`.

## Как играть

**Хост:**
1. Меню игры: кнопка **NEW GAME** (или консоль `~` → `mp host`), можно через лаунчер.
2. В чат придёт: `СЕССИЯ СОЗДАНА. Код: XXXXXXXXX` — код **автоматически в буфере обмена**.
3. Передай код другу.

**Клиент:**
1. Меню игры: кнопка **JOIN** → впиши код в окошке → **ВОЙТИ** (или `mp join <код>` в консоли).
2. Войдёшь в мир хоста: время суток, погода подтянутся автоматически.

## Что уже работает

| Фича | Статус |
|---|---|
| **Steam-лобби + вход по коду сессии** (схема BeerMP LobbyCodeParser) | ✅ |
| **P2P-пакеты Steamworks** (reliable для событий, unreliable 20 Гц для состояний) | ✅ |
| Позиции/повороты игроков + интерполяция аватаров | ✅ |
| Чат (F1 открыть, Enter отправить, Esc закрыть) | ✅ |
| Сидение в машине / выход (авто-детект по родителю Fsy) | ✅ |
| Курение — клавиша T | ✅ |
| Питьё воды — клавиша G | ✅ |
| Руль сатсы (угол, при изменении > 3°) | ✅ |
| Валидация пидов на хосте (подмена пидов) | ✅ |
| PlayerLeft + чистка "призраков" (reconcile по списку) | ✅ |
| HostLost: хост вышел — чистка аватаров и переход в офлайн | ✅ |
| Повтор `Hello` клиентом (2с) — защита от потери первого пакета | ✅ |
| Код сессии в буфере обмена (у хоста) | ✅ |
| **UI главного меню**: МП-панель + кнопки **NEW GAME** (хост, код в буфере) и **JOIN** (окно ввода кода) | ✅ |
| **Лаунчер** (dist\MPRecodeLauncher.exe): проверка версии на GitHub → докачка мода → запуск игры + Play! | ✅ |
| **ВРЕМЯ СУТОК** (день/час/минуты, хост → всем + снапшот при входе) | ✅ |
| **ПОГОДА** (облака/тип погоды FSM CloudSystem, снапшот + поллинг 4с) | ✅ |
| **Клиент НЕ грузит своё время/погоду** (стейты "Load"/"Load game" off) | ✅ |
| Каркас FSM-событий каталога (двери/пожар) | инт. |
| `mp scanworld [радиус]` — каталог FSM мира | ✅ |
| `mp dumpcar` — карта узлов машины | ✅ |

## Тесты

```
test\MPRTest.exe 4        # протокол пидов/кадров/действий (TCP headless): PASS=8
test\MPRDllTest.exe       # real-классы из DLL: LobbyCode/Writer/Reader/Hash/HostLost: PASS=13
```
Steam headless тест невозможен (SteamAPI требует живого клиента) — lobby/P2P проверяется в игре.

## Команды (консоль MSCLoader, тильда ~)

```
mp host            – создать Steam-сессию (код придёт в чат и в буфер)
mp join <код>      – войти в сессию по коду
mp code            – показать/копировать код (только у хоста)
mp leave           – выйти из сессии
mp status          – статус (режим/игроки/код)
mp name <ник>      – сменить ник и сохранить
mp maxplayers N    – максимум игроков (2..16, по умолчанию 8)
mp bots N          – N косметических ботов (только визуально)
mp clear           – убрать ботов
mp scanworld [r]   – каталог FSM мира (для v0.4)
mp dumpcar         – карта узлов машины
```

В чате (F1): `/bots`, `/clearbots`, `/pos`.

## Где что лежит

```
E:\My Summer Car - MP Copy\
  ├ Mods\MSCMPRecode.dll             – собранный мод (СТРОГО в корне Mods, иначе MSCLoader не увидит)
  ├ MPRecode\src\MpSteam.cs          – Steam-слой: лобби/код/коллбэки/P2P-хост/клиент
  ├ MPRecode\src\MpManager.cs        – менеджер МП (режимы, аватары, чат, события)
  ├ MPRecode\src\MpWorldSync.cs      – синхронизация мира (время/погода/FSM)
  ├ MPRecode\src\MPNet.cs            – протокол (Packet 1..14, PacketWriter/Reader, NetUtil)
  ├ MPRecode\src\MpConfig.cs         – конфиг (joincode, nickname)
  ├ MPRecode\src\MpMenuUI.cs         – UI меню + перехват кнопок Continue/NewGame
  ├ MPRecode\src\MpCommand.cs        – консольные mp-команды
  ├ MPRecode\legacy\                 – старые TCP-хост/клиент/боты (не компилируются)
  ├ MPRecode\beermp-src\             – декомпилированный BeerMP (референс Steam / кодов)
  ├ MPRecode\build.bat               – сборка (выводит dll сразу в Mods\)
  └ MPRecode\test\                   – headless-тесты
E:\My Summer Car\                    – оригинал, НЕ трогаем (там играешь ты)
```

## Как работает вход по коду (v0.3.0)

Схема скопирована у BeerMP (`SteamNet` + `LobbyCodeParser`), обёртка Steamworks — та же, что у игры (`Assembly-CSharp-firstpass`):

- **Создание**: `SteamMatchmaking.CreateLobby(k_ELobbyTypePublic, N)` → `LobbyCreated_t` → `LobbyCode.Encode(lobbyId)` — код это счёт 6-битных символов из [0-9A-Za-z_-]; одновременно `SetLobbyData("mprver","0.3")`.
- **Вход**: `RequestLobbyData(CSteamID(decode(code)))` → коллбэк `LobbyDataUpdate_t` → `JoinLobby` → `LobbyEnter_t` (=1 ok).
- **P2P**: обе стороны дергают `AcceptP2PSessionWithUser` на `P2PSessionRequest_t`; каждый кадр `SteamAPI.RunCallbacks()` + `IsP2PPacketAvailable/ReadP2PPacket` (главный поток, `MpManager.Tick`).
- **Роли**: хост = pid 0, клиенты 1..N (подмена пидов на хосте, как в TCP-версии). Клиент шлёт только хосту; хост раздаёт всем.
- **HostLost**: уход хоста из лобби (`LobbyChatUpdate_t` state 2, владелец) → клиент чистит аватары/время/погоду → офлайн.

## Как работает синхронизация мира (v0.2, сохранено)

- **Хост — единственный источник правды.** Каждые 2с проверяет время (`MAP/SUN/Pivot/SUN`, FSM "Color": GlobalDay/Time/Minutes) и каждые 4с погоду (`MAP/CloudSystem/Clouds`, FSM "Weather": Offset/PosX/PosZ/Rotation/X/Z/WeatherCloudID/WeatherType). При изменении рассылает `WorldEvent`.
- **При входе нового игрока** хост сразу даёт снапшот: время + погоду как у себя (`OnClientReady`).
- **Клиент** отключает свои FSM-стейты загрузки мира ("Load" у солнца, "Load game" у облаков), применяет полученное.
- **Произвольные FSM-события** (двери, выключатели, пожар) передаются парой (хеш имени объекта, имя события). Каталог собирается по `mp scanworld`.

## Roadmap (v0.4+)

- [ ] Каталог FSM-событий по `mp scanworld` → синхронизация горящего дома, радиостанций, освещения
- [ ] Кар-синк в стиле BeerMP (NetVehicle/FsmNetVehicle): физика и приборы машин
- [ ] Реальная посадка в машину игрока (не только флаг)
- [ ] Steam rich presence: "Играет MP: N игроков"
- [ ] Проверка роутеров: P2P relay (Steam сети) в строгих NAT (учёт Steam Networking — GetP2PSessionState)

## Как проверить самому (ты)

1. `E:\My Summer Car - MP Copy\mysummercar.exe` (мод уже в Mods)
2. Консоль `~` → `mp host` → код появится в чате (уже в буфере).
3. `mp status` — «ХОСТ (Steam) | игроков: 1 | код: …».
4. Со второй копии (другая машина/окно) `mp join <код>`.
5. Прогони `mp scanworld 40` и `mp dumpcar` (сидя в машине) — вывод нужен для v0.4 (FSM-каталог «всего»).

# Сборка из исходников

[English](build.md) | **Русский**

Каждая платформа собирается отдельно. Движок туннеля везде идёт первым: приложение подхватывает уже собранный бинарник.

## Windows

Понадобятся [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), [WiX](https://wixtoolset.org) для установщика и git.

### 1. Забрать сабмодули

Две нужные сборке вещи подключены git-сабмодулями и в основной репозиторий не входят: движок AmneziaWG для Windows (`amneziawg-windows`) и `sing-tun` - пользовательский сетевой стек, на котором стоит шлюз точки доступа:

```powershell
git submodule update --init --recursive
```

### 2. Собрать движок туннеля

Чтобы туннель работал и на Windows 7, движок собирается версией Go с поддержкой Windows 7:

```powershell
amneziageo-windows\tools\build-engine-win7.ps1
```

Первый запуск сам скачивает всё необходимое - Go, llvm-mingw и wintun через `build.cmd` сабмодуля, плюс отдельный Go для Windows 7, - проверяет загрузку по контрольной сумме и кладёт готовый `tunnel.dll` туда, откуда его берёт приложение. Ключи:

- `-Arch x64|x86|arm64` - целевая архитектура, по умолчанию `x64`;
- `-Upstream` - собрать стоковым Go, без поддержки Windows 7;
- `-Force` - перекачать и пересобрать тулчейн.

### 3. Собрать шлюз точки доступа

`gateway.exe` проводит клиентов точки доступа через пользовательский стек, чтобы отправленное ими уходило с машины так же, как её собственный трафик. Это Go-модуль в `amneziageo-windows\gateway`, стоящий на соседнем сабмодуле `sing-tun`:

```powershell
amneziageo-windows\tools\build-gateway.ps1
```

Go берётся из `PATH`, а если там его нет - из тулчейна, скачанного на шаге 2 в `amneziawg-windows\.deps`. Результат кладётся в `gateway\bin\<арх>\gateway.exe`, откуда его берёт приложение; без него сборка приложения останавливается и называет этот шаг. Ключи:

- `-Arch x64|arm64|both` - целевая архитектура, по умолчанию обе.

### 4. Собрать приложение и установщик

```powershell
# приложение и служба
dotnet build amneziageo-windows\AmneziaGeo.Windows.Ui\AmneziaGeo.Windows.Ui.csproj -c Release

# установщик -> dist\AmneziaGeo-<версия>-win-<арх>-<payload>.exe
amneziageo-windows\installer\AmneziaGeo.Windows.Installer.Bundle\build-installer.ps1
```

По умолчанию собирается один вариант: `x64`, framework-dependent - на целевой машине нужен установленный .NET 10 Desktop Runtime. Ключи сборщика, у каждого есть короткий алиас, `-h` покажет весь список:

- `-v, -Version N.N.N.N` - версия бандла и бинарей, иначе `0.0.1.<число коммитов>`;
- `-a, -Arch x64,arm64` - архитектуры списком или `all`;
- `-p, -Payload fdd,scd` - тип поставки: `fdd` требует установленного runtime и легче, `scd` несёт runtime внутри;
- `-c, -Configuration Debug|Release`;
- `-pre, -Prerelease` - вшить бета-канал обновлений;
- `-r, -Rebuild` - очистить перед сборкой;
- `-l, -ListOnly` - показать матрицу сборки и выйти.

### Запуск в отладке

Лаунчер одной командой поднимает бэкенд-агент и интерфейс в одном процессе. Запускать из консоли от имени администратора - он ставит службу и правила WFP и поднимает туннель; нужны собранные на шагах 2 и 3 `tunnel.dll` и `gateway.exe`.

```powershell
dotnet run --project amneziageo-windows\tools\AmneziaGeo.Windows.Launcher
```

Без флагов поднимаются обе части. Флаги: `--service` - только агент, `--ui` - только интерфейс, `--target <имя>` - сразу выбрать конфигурацию, `--config <путь.conf>` - зарегистрировать wg-quick конфиг и запуститься на нём.

## Linux

```bash
git submodule update --init --recursive
amneziageo-linux/tools/build-deb.sh --arch amd64,arm64
```

Нужны .NET SDK и Go, целевой машине - ничего: пакеты self-contained. Результат кладётся в `dist/`. Ключи: `--version N.N.N.N` (иначе `0.0.1.<число коммитов>`), `--arch amd64,arm64`, `--out <каталог>`, `--no-gui`, `--debug`.

Список альтернатив `libicu` в скрипте перечисляет ICU-пакеты, которые могут оказаться в целевом дистрибутиве: новый выпуск дистрибутива добавляет туда одну запись.

## Android

Сначала нативный движок, потом APK:

```bash
amneziageo-android/tools/build-engine-android.sh
amneziageo-android/tools/build-apk.sh --abi android-arm64
```

Движок собирается тулчейном Android NDK в `AmneziaGeo.Android.Engine/native/<abi>/libamneziawg-go.so`; сабмодуль `amneziawg-go` при этом не правится - точки входа c-shared лежат в отдельном модуле рядом. Ключи `build-apk.sh`:

- `--config Release|Debug` - по умолчанию `Release`, он AOT-компилируется и на слабом телевизоре стартует вдвое быстрее;
- `--version N.N.N.N` - версия пакета, все четыре числа складываются в versionCode;
- `--abi android-arm,android-arm64,android-x64` - список ABI, по умолчанию все объявленные проектом;
- `--update-url <url>` - адрес манифеста обновлений, вшиваемый в пакет;
- `--prerelease` - оставить сборку на бета-канале.

Подпись берёт отладочный ключ SDK, если не задан `ANDROID_KEYSTORE`; вместе с ним нужны `ANDROID_KEY_ALIAS`, `ANDROID_STORE_PASS` и `ANDROID_KEY_PASS`.

На Windows те же скрипты есть в варианте PowerShell: `build-engine-android.ps1` и `build-apk.ps1`.

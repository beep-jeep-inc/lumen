namespace Pult.App;

/// Все тексты интерфейса. На этапе 5 появится английский — поэтому только отсюда.
public static class Strings
{
    public const string AppName = "Lumen";
    public const string AdminSetupTitle = "Помощник администратора";
    public const string AdminSetupText = "Lumen поставит маленького помощника в C:\\Program Files\\Pult. Он нужен, чтобы ставить драйверы, делать точку восстановления и чистить системные папки. Сейчас Windows один раз спросит «Да».";
    public const string ConfigRestored = "Настройки восстановлены";

    // --- Обслуживание ---
    public const string TileNotChecked = "Ещё не проверяли", TileChecking = "Проверяю…", TileQueued = "Ждёт очереди", TileManual = "Нужна ручная проверка — откройте раздел";
    public const string TileHeavyDrivers = "Проверяется отдельно — поиск займёт 1–3 минуты", TileHeavyPrograms = "Проверяется отдельно — займёт около минуты (нужна сеть)";
    public const string TileHeavyCleanup = "Проверяется отдельно — сканирование 1–3 минуты", TileHeavySpace = "Проверяется отдельно — сканирование диска 1–3 минуты";
    public const string TileRepairNotRun = "Запускается отдельно — в самом разделе";
    public const string QuickAll = "Проверить всё", QuickStop = "Остановить", QuickHint = "Несколько секунд · только чтение, ничего не меняется";
    public const string SecDrivers = "Драйверы", SecDriversHint = "Центр обновления Windows и официальные приложения производителей. Драйверы не ставятся сами и не входят в «обновить всё».";
    public const string SecLibraries = "Системные библиотеки", SecLibrariesHint = "Visual C++, DirectX, .NET, Java: чего не хватает для программ и игр.";
    public const string SecPrograms = "Программы", SecProgramsHint = "Обновления из официального каталога winget (сверку файлов делает сам winget).";
    public const string SecCleanup = "Очистка", SecCleanupHint = "Кеши и временные файлы восстанавливаются сами. Кук, паролей, истории и закладок здесь нет.";
    public const string SecSpace = "Что занимает место", SecSpaceHint = "Только чтение. Личные файлы уходят только в Корзину и только отмеченные.";
    public const string Check = "Проверить", CheckDrivers = "Найти драйверы", Scan = "Сканировать", Cancel = "Отмена";
    public const string Reading = "Читаю…", Cancelled = "Отменено", Failed = "Не получилось: ";
    public const string InstallChecked = "Установить выбранные", InstallOrUpdate = "Установить / обновить выбранные", UpdateChecked = "Обновить выбранные";
    public const string CheckAll = "Отметить все", UncheckAll = "Снять все", DontUpdate = "Не обновлять", RestoreHidden = "Вернуть скрытые", Restore = "Вернуть";
    public const string Gpus = "Видеокарты", NoGpu = "Видеокарт не нашёл", OldDriver = "Старый драйвер", DriverFine = "Свежий драйвер";
    public const string WinUpgradeTitle = "Доступно обновление Windows: {0}", WinUpgradeDetail = "Сейчас {0}. Большое обновление системы: 30–60 минут и несколько перезагрузок. Ставится в Центре обновления Windows — когда будет время.", WinUpgradeUnknownCurrent = "версия неизвестна";
    public const string OpenVendorApp = "Открыть ", OpenWindowsUpdate = "Открыть Центр обновления";
    public const string DeviceErrors = "Устройства с ошибками", DeviceErrorsSub = "что говорит Windows", NoDeviceErrors = "Ошибок нет";
    public const string WuDrivers = "Драйверы из Центра обновления", WuDriversSub = "по умолчанию ничего не отмечено";
    public const string WuNotChecked = "Нажми «Найти драйверы» — поиск занимает до минуты", WuNone = "Новых драйверов нет", WuSearching = "Ищу в Центре обновления Windows — обычно 1–3 минуты…";
    public const string WuFound = "Найдено драйверов: {0}. Отметь нужные — ничего не ставится без отметки.", WuFailed = "Поиск не удался: ";
    public const string RebootTagNever = "без перезагрузки", RebootTagAlways = "нужна перезагрузка", RebootTagMaybe = "может понадобиться перезагрузка";
    public const string RebootNotNeededAsk = "Перезагрузка не понадобится.", RebootMaybeAsk = "Перезагрузка может понадобиться для: {0}. Компьютер сам не перезагрузится.";
    public const string RebootNotNeededDone = "Перезагрузка не нужна — драйверы уже работают.";
    public const string DriverConfirm = "Будет создана точка восстановления и установлено {0} {1}. {2}";
    public const string DriverConfirmNoRestore = "Установлено будет {0} {1}. Точка восстановления создаваться НЕ будет — откатить драйверы через неё будет нельзя. {2}";
    public const string RestoreBeforeInstall = "Создать точку восстановления перед установкой", RestoreBeforeInstallHint = "Рекомендуется: драйверы можно будет откатить";
    public const string FirmwareWarning = "⚠ Обновление BIOS (прошивка): ставь только от сети и не выключай ноутбук; точка восстановления его не откатит; прошивка применится при перезагрузке — экран может несколько минут быть чёрным.";
    public const string FirmwareBlock = "Прошивка BIOS", FirmwareBlockSub = "отдельно от драйверов, по умолчанию не отмечена";
    public const string FirmwareNoticeTitle = "Не ставь прошивку BIOS, если не уверен", FirmwareNotice = "Обычным драйверам она не нужна. Ошибка при обновлении может сделать компьютер неработоспособным. Ставь только если знаешь, зачем, и только от сети.";
    public const string FirmwareConfirm = "Среди выбранного — прошивка BIOS. Ноутбук должен быть на зарядке.";
    public const string FirmwareOnBattery = "Подключи зарядку — прошивку BIOS от батареи не ставлю.";
    public const string HelperNeededDrivers = "Без помощника администратора драйверы не ставятся — ничего не установлено.";
    public const string HelperNeededClean = "Без помощника администратора системные пункты не очищаются — они пропущены.";
    public const string DriversInstalling = "Устанавливаю драйверы…", DriversDone = "Драйверы установлены";
    public const string RebootNeeded = "Нужна перезагрузка, чтобы драйверы заработали. Сам Lumen ничего не перезагрузит. Windows тоже может предложить перезагрузку — её можно отложить в Параметры → Центр обновления Windows.", RebootNow = "Перезагрузить сейчас", RebootLater = "Позже",
                                RebootPostponed = "Перезагрузка отложена — драйверы заработают после следующего включения ПК.", RebootCancel = "Отменить перезагрузку";
    public const string RebootAsk = "Компьютер перезагрузится через минуту. Сохрани открытую работу — до перезагрузки её можно отменить кнопкой. Перезагрузить?";
    public const string RebootScheduled = "Перезагрузка через минуту. Передумал — нажми «Отменить перезагрузку».", RebootFailed = "Не удалось запланировать перезагрузку.";
    public const string RebootCancelled = "Перезагрузка отменена.", RebootCancelFailed = "Отменить не вышло — возможно, она уже началась.";
    public const string AdminDeclined = "Ты отказался в окне помощника — ничего не сделано.", AdminDeclinedTimeout = "Окно помощника не дождалось ответа (5 минут) и закрылось как «Нет» — ничего не сделано.";
    public const string AdminTaskMissing = "Помощник устарел: ему не хватает служебной задачи. Нажми «Обновить помощника».";
    public const string RestoreDisabled = "Защита системы выключена — точку восстановления создать нельзя, драйверы не ставились. Включи защиту системы в Параметрах Windows.";
    public const string AdminNoAccount = "Нужна учётная запись администратора Windows — у тебя обычная. Эти функции недоступны.";
    public const string AdminOtherUser = "Помощник установлен для другого пользователя Windows — переустанови его (одно окно «Да»). Нажми «Обновить помощника».";
    public const string HelperWrongFolder = "Помощника нельзя ставить в одну папку с Lumen (C:\\Program Files\\Pult). Установи Lumen в другое место, например %LOCALAPPDATA%\\Programs\\Pult, и повтори.";
    public const string WrongInstallFolder = "Lumen установлен не туда (C:\\Program Files\\Pult — эта папка для помощника). Переустанови его: publish.ps1 -Install или установщик с папкой в %LOCALAPPDATA%\\Programs\\Pult.";
    public const string RunningElevated = "Lumen запущен от администратора — лучше открывать его обычным способом: помощник администратора сам поднимет права там, где нужно.";
    public const string AdminNotInstalled = "Помощник администратора не установлен.", AdminRefused = "Помощник отказал.";
    public const string AdminBusy = "Помощник занят обслуживанием (драйверы, очистка, починка Windows или обновление программ) — попробуй позже.";
    public const string CancelAfterCurrent = "Отменю после текущей программы…";
    public const string CleanConfirmBin = "Корзина будет очищена безвозвратно.", CleanConfirmProjects = "Отмечены проекты с незакоммиченными изменениями или без git: {0}.";
    public const string CancelledFreed = "Отменено · освобождено {0}", SkippedBusyFiles = "пропущено файлов: {0} — заняты", CategoryNotFound = "категория не найдена";
    public const string UpdateHelper = "Обновить помощника", HelperUpdated = "Помощник обновлён — повтори действие.", HelperNotUpdated = "Помощник не обновлён.";
    public const string HelperNoFile = "Не нашёл PultAdmin.exe рядом с Lumen — обновить помощника нечем. Поставь Lumen заново (publish.ps1 -Install).";
    public const string HelperNotAot = "PultAdmin.exe рядом с Lumen — не сборка NativeAOT, обновлять помощника им не буду.";
    public const string HelperUpdateAsk = "Обновить помощника администратора версией, которая лежит рядом с Lumen? Windows один раз спросит «Да».";
    public const string WingetChecking = "Проверяю обновления в winget (до минуты)…", WingetMissing = "winget не найден — нужен «Установщик приложений» из Microsoft Store.";
    public const string WingetOld = "winget устарел — обнови «Установщик приложений» (App Installer) в Microsoft Store.";
    public const string WingetFailed = "winget не ответил: ", WingetBusy = "Проверка winget уже идёт.";
    public const string InstallAppInstaller = "Установить App Installer", AppInstallerUrl = "ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1";
    public const string Updating = "Обновляю {0} ({1} из {2})…", Installing = "Устанавливаю {0} ({1} из {2})…", Working = "Работаю…";
    public const string CloseIt = "закрой программу и нажми «Обновить» ещё раз", Done = "Обновлено", Of = "из", SkippedOpen = "пропущено (программа открыта)", WithError = "с ошибкой";
    public const string LibrariesConfirm = "Установить или обновить библиотек: {0}? Всё идёт через winget, лишнего не ставится.";
    public const string ProgramsConfirm = "Обновить программ: {0}? Каждая обновляется по одной; открытые программы будут пропущены.";
    public const string LibrariesFound = "Можно установить или обновить: {0}.", LibrariesFine = "Всё на месте, обновлений нет.";
    public const string ProgramsFound = "Обновлений: {0}. Ничего не обновляется без отметки.", ProgramsFine = "Все программы свежие.", ProgramsNotChecked = "Нажми «Проверить» — список берётся из winget";
    public const string NotInstalled = "Не установлена", Installed = "Установлена", EndOfSupport = "Поддержка закончилась";
    public const string Scanning = "Сканирую…", CleanScanning = "Сканирую систему, браузеры и проекты (может занять минуту)…", Looking = "Смотрю: ", FindingProjects = "Ищу проекты на Рабочем столе…";
    public const string CleanNotScanned = "Нажми «Сканировать» — Lumen только посмотрит, что можно очистить, и ничего не удалит", CleanScanned = "Отметь, что очистить, и нажми «Удалить отмеченное».", CleanNothing = "Чистить нечего.";
    public const string DeleteChecked = "Удалить отмеченное", NoSize = "без размера", CleanConfirm = "Удалить отмеченное: {0}?\n\nКеши и временные файлы восстановятся сами. Занятые файлы будут пропущены.";
    public const string CleanConfirmHelper = "Для пунктов со щитом появится окно помощника администратора.", CleanAdminProgress = "Очищаю", Cleaning = "Очищаю…", CleaningOne = "Очищаю: {0} ({1} из {2})…";
    public const string Freed = "Освобождено", SkippedFiles = "пропущено файлов", Busy = "заняты", Close = "закрой", WontTouchOpen = "пока открыта, не трогаю", NeedsHelper = "через помощника администратора", Open = "Открыта";
    public const string OpenWindowsCleanup = "Открыть очистку Windows", ProjectsHeader = "Мусор в проектах", ProjectsSub = "node_modules, bin, obj — пересоберутся сами";
    public const string Uncommitted = "не сохранено в git", NoGit = "нет git", ShowInExplorer = "Показать";
    public const string SpaceNotScanned = "Нажми «Сканировать» — Lumen посчитает папки диска, найдёт крупные файлы и старые установщики", SpaceScanning = "Считаю папки диска (несколько минут)…", BigFilesScanning = "Ищу крупные файлы…";
    public const string SpaceScanned = "Готово. Отметь ненужные файлы — они уйдут только в Корзину.", Counting = "Считаю: ";
    public const string BiggestFolders = "Самые большие папки", BiggestFoldersSub = "системные папки только для просмотра";
    public const string BigFiles = "Крупные файлы", BigFilesSub = "в твоём профиле, от 300 МБ", OldInstallers = "Старые установщики", OldInstallersSub = "в «Загрузках», старше 30 дней";
    public const string Duplicates = "Дубликаты", DuplicatesSub = "в Загрузках, Документах, Рабочем столе, Изображениях, Видео и Музыке", NothingFound = "Ничего не нашёл";
    public const string FindDuplicates = "Найти дубликаты", DupScanning = "Ищу дубликаты…", DupNone = "Дубликатов нет.", DupFound = "Групп дубликатов: {0}, лишнего места: {1}.";
    public const string DupAllChecked = "Все копии «{0}» отметить нельзя — одна должна остаться. Отметка не поставлена.";
    public const string SeriesFiles = "Файлы серии", SeriesKeepOne = "Файлов серии не отмечено: {0} — из группы одинаковых файлов должна остаться хотя бы одна копия.";
    public const string DupMarkIn = "Убрать копии из «{0}»", DupMarkExtra = "Убрать лишние копии",
                        DupPairHint = "Выбери папку, из которой убрать копии: отметятся файлы из неё, а такие же в другой папке останутся.", DupSameHint = "В каждой группе останется один файл, лишние отметятся.", DupMoreGroups = "Показать ещё {0} (осталось групп: {1})", DupMorePairs = "…и ещё пар папок: {0}", Collapse = "Свернуть", Show = "Показать";
    public const string DupDetailTitle = "Подробный список по файлам", DupDetailClosed = "Подробный список по файлам скрыт (групп: {0})", DupDetailCap = "Показано групп: {0} из {1}; все остальные тоже отмечаются кнопками пар выше.";
    public const string DupMarked = "Отмечено: {0} в «{1}», {2}. Из каждой группы осталась хотя бы одна копия. Проверь и нажми «В Корзину».", DupNothingToMark = "Здесь больше нечего отмечать: в каждой группе должна остаться хотя бы одна копия.";
    public const string ToRecycle = "В Корзину выбранное", TrashConfirm = "Переместить в Корзину: {0} (всего {1})? Их можно будет вернуть из Корзины.", Moving = "Перемещаю в Корзину…";
    public const string MovedToBin = "В Корзину перемещено: {0} ({1})", NotMoved = "не перемещено";
    // --- Автозагрузка ---
    public const string SecStartup = "Автозагрузка", SecStartupHint = "Что запускается вместе с Windows. Выключение ничего не удаляет: запись остаётся, её можно включить обратно.";
    public const string StartupNotRead = "Нажмите «Проверить» — Lumen покажет, что запускается вместе с Windows. Ничего не изменится.", StartupReading = "Читаю автозагрузку…";
    public const string StartupEmpty = "В автозагрузке ничего нет.", StartupOn = "Включено", StartupOff = "Выключено", StartupSelf = "Это Lumen";
    public const string StartupSelfDetail = "Это Lumen — управляется галочкой внизу меню.", StartupDoOff = "Выключить", StartupDoOn = "Включить";
    public const string CloseWhileBusy = "Идёт работа в разделе «{0}». Если закрыть Lumen, начатое помощником доделается само, но результат ты не увидишь, а переустановка программы может остаться незаконченной. Закрыть?";
    public const string StartupChanging = "Меняю…", StartupConfirmInHelper = "Подтверди в окне помощника…", StartupHelperNeeded = "Для записей «для всех пользователей» нужен помощник администратора — без него их менять нельзя.";
    public const string StartupShieldTip = "Это меняет помощник администратора (он сам покажет окно подтверждения; меняется только «включено/выключено»)";
    public const string StartupMissingPill = "Файл не найден", StartupMissingNote = "Файл программы не найден — эта запись ничего не запустит. Сам файл Lumen не трогает; запись можно выключить.", StartupImportantLine = "Важная запись ({0}): перед выключением Lumen спросит.";
    public const string StartupNotes = "Выключенная программа не запустится при следующем входе в Windows. Это то же самое, что переключатель в «Диспетчере задач».";
    // --- Здоровье дисков ---
    public const string SecDisks = "Здоровье дисков", SecDisksHint = "Общее состояние дисков и занятое место на них. Износ и температуру Windows без прав администратора не отдаёт — их читает помощник по кнопке «Подробно». Только чтение, ничего не меняется.";
    public const string DisksNotRead = "Нажмите «Проверить» — Lumen спросит у Windows, как себя чувствуют диски.", DisksReading = "Спрашиваю у Windows про диски…";
    public const string DisksNone = "Windows не показала ни одного диска.", DisksNoCounters = "Износ и температуру без прав администратора Windows не отдаёт — показано только общее состояние. Нажми «Подробно» — это прочитает помощник (только чтение, окно «Да» не нужно).";
    public const string DisksDetails = "Подробно (износ и температура)", DisksDetailsAsk = "Спрашиваю помощника про износ и температуру…", DisksDetailsOk = "Подробные данные получены.", DisksDetailsEmpty = "Помощник ответил, но Windows не отдала счётчики дисков.", DisksNoHelper = "Подробные данные читает помощник администратора, а он не установлен. Его можно поставить в разделе «Драйверы» (кнопка «Найти драйверы» предложит это сама).";

    // --- Починка Windows ---
    public const string SecRepair = "Починка Windows", SecRepairHint = "Проверка и восстановление системных файлов Windows встроенными средствами (DISM и sfc). Запускается только по кнопке.";
    public const string RepairStart = "Проверить и починить системные файлы";
    public const string RepairNotRun = "Lumen запустит две встроенные проверки Windows: сначала DISM восстановит хранилище компонентов (нужен интернет), затем sfc проверит системные файлы и вернёт на место испорченные. Это займёт от 10 до 45 минут; компьютером можно пользоваться. Остановить проверку из Lumen нельзя. Перезагрузка — только по вашей кнопке.";
    public const string RepairConfirm = "Проверить и починить системные файлы Windows?\n\nЭто займёт 10–45 минут. Для DISM нужен интернет. Компьютером можно пользоваться, но Lumen ждёт окончания проверки, и остановить её отсюда нельзя. Перезагрузка сама не начнётся.";
    public const string RepairRunning = "Проверяю и чиню системные файлы (до 45 минут, остановить нельзя)";
    public const string RepairAlreadyRunning = "Проверка системных файлов уже идёт (запущен sfc или DISM) — дождитесь её окончания.";
    public const string RepairLogNote = "Подробности — в журнале C:\\Windows\\Logs\\CBS\\CBS.log. Если файлы не исправились, поможет точка восстановления Windows.";
    public const string RepairRebootNote = "Сам Lumen ничего не перезагрузит — перезагрузка только по кнопке ниже.";
    public const string RepairTileMany = "нужно внимание", RepairTileReboot = "нужна перезагрузка", RepairTileNone = "повреждений нет", RepairTileUnknown = "проверка по кнопке";

    // --- Winget: что делать после неудачи, Python/.NET в работе ---
    public const string JobFailedPill = "Не удалось", WingetRetryAdmin = "Повторить с правами администратора", WingetReinstall = "Переустановить", WingetInstallAgain = "Установить заново";
    public const string WingetAdminRunning = "Помощник администратора выполняет winget (до 30 минут; сначала он спросит «Да/Нет»)";
    public const string WingetAdminDone = "«{0}»: готово (с правами администратора).", WingetAdminDeclined = "Ты отказался в окне помощника — ничего не выполнено.";
    public const string ReinstallConfirm = "Переустановить «{0}»?\n\nПрограмма будет удалена и поставлена заново. Настройки и данные в папке переносной (portable) версии могут пропасть — сохрани их заранее, если они важны.";
    public const string Reinstalling = "Переустанавливаю {0}…", UninstallFailed = "не удалось удалить — ";
    public const string RuntimeInUseAsk = "Эти программы могут перестать работать на 2–3 минуты обновления:\n\n{0}\n\nОбновить сейчас?";
    public const string SkippedInUse = "пропущено: используется ({0})", SkippedInUseCount = "пропущено (используется)", UpdateAloneHint = "Обнови его отдельно — Lumen спросит.";

    public static string Uptime(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин" : $"{Math.Max(1, t.Minutes)} мин";
}

(() => {
  const translations = {
    ru: {
      'meta.title': 'Nexora — игровой лаунчер для Windows',
      'meta.description': 'Nexora — лаунчер для Windows: поиск игр в каталоге, загрузки через BitTorrent и HTTP, проверка файлов и своя библиотека.',
      'nav.features': 'Возможности',
      'nav.screens': 'Интерфейс',
      'nav.download': 'Загрузка',
      'nav.faq': 'Вопросы',
      'nav.downloadCta': 'Скачать',
      'hero.kicker': 'Игровой лаунчер для Windows',
      'hero.title': 'Каталог, загрузки и библиотека в одном лаунчере',
      'hero.lede': 'Nexora ищет игры в подключённых источниках, качает их через BitTorrent или HTTP с докачкой и запускает из твоей библиотеки.',
      'hero.download': 'Скачать для Windows',
      'hero.more': 'Что внутри',
      'hero.status': 'Версия',
      'hero.shot': 'Главный экран: каталог и подборки',
      'fact.lang': 'языка интерфейса',
      'fact.hash': 'проверка файлов',
      'fact.torrent': 'торрент-движок',
      'fact.os': '10 и 11, x64',
      'features.kicker': 'Возможности',
      'features.title': 'Что Nexora делает сам',
      'features.lede': 'От поиска до кнопки «Играть» — без ручной распаковки архивов и проверки хешей.',
      'f1.title': 'Поиск по источникам',
      'f1.text': 'Встроенный каталог Rutor и свои JSON-источники: подборки, фильтры и поиск в реальном времени.',
      'f2.title': 'BitTorrent внутри',
      'f2.text': 'Движок rqbit на Rust: magnet-ссылки, .torrent, fast resume и лимиты скорости.',
      'f3.title': 'HTTP с докачкой',
      'f3.text': 'Range-запросы, временная папка staging и атомарная установка после проверки.',
      'f4.title': 'Проверка SHA-256',
      'f4.text': 'Хеши финальных файлов сверяются после распаковки. Не совпало — установка отменяется.',
      'f5.title': 'Своя библиотека',
      'f5.text': 'Статусы установки и обновлений, запуск игры, проверка файлов, папка и удаление.',
      'f6.title': 'Настройки под себя',
      'f6.text': 'Папка библиотеки, лимиты скорости, очистка кэша, язык интерфейса и цвет акцента.',
      'screens.kicker': 'Интерфейс',
      'screens.title': 'Один лаунчер на весь путь',
      'screens.lede': 'Каталог, библиотека, карточка игры и настройки собраны в одном тёмном интерфейсе.',
      'screen.library.title': 'Библиотека',
      'screen.library.text': 'Установленные игры и быстрый запуск.',
      'screen.details.title': 'Карточка игры',
      'screen.details.text': 'Размер, версия, характеристики и хранилище.',
      'screen.settings.title': 'Настройки',
      'screen.settings.text': 'Папки, скорость, язык и акцент.',
      'flow.kicker': 'Как это работает',
      'flow.title': 'Три шага до игры',
      'flow.lede': 'Nexora ведёт от поиска до запуска, сохраняя прогресс загрузки между сессиями.',
      'step1.title': 'Найди игру',
      'step1.text': 'Введи название в поиск или открой подборку в каталоге.',
      'step2.title': 'Скачай',
      'step2.text': 'Nexora выберет источник и покажет скорость, прогресс и остаток.',
      'step3.title': 'Запускай',
      'step3.text': 'Проверь целостность файлов и открой игру из библиотеки.',
      'download.kicker': 'Загрузка',
      'download.title': 'Скачать Nexora',
      'download.lede': 'Установщик для Windows x64. .NET ставить отдельно не нужно.',
      'download.current': 'Текущая сборка',
      'download.ready': 'Готово к установке',
      'download.size': 'Размер',
      'download.updated': 'Файл изменён',
      'download.file': 'Файл',
      'download.button': 'Скачать установщик',
      'download.external': 'Откроется страница последнего релиза на GitHub.',
      'download.unsigned': 'Установщик не подписан цифровым сертификатом, поэтому Windows может показать предупреждение об издателе.',
      'download.before': 'Перед установкой',
      'download.req1': 'Windows 10 build 18362+ или Windows 11, x64',
      'download.req2': 'Интернет для поиска и загрузок',
      'download.req3': 'Место на диске под библиотеку игр',
      'download.req4': '.NET 8 не нужен: сборка self-contained',
      'download.releases': 'Все релизы на GitHub',
      'faq.kicker': 'Вопросы',
      'faq.title': 'Перед первым запуском',
      'faq.lede': 'Коротко о требованиях, папках и обновлениях.',
      'faq.q1': 'Нужно ли ставить .NET?',
      'faq.a1': 'Нет. Nexora собирается как self-contained приложение и несёт нужные компоненты с собой.',
      'faq.q2': 'Куда устанавливаются игры?',
      'faq.a2': 'По умолчанию в %USERPROFILE%\\Games\\Nexora. Папку можно выбрать при установке и изменить в настройках.',
      'faq.q3': 'Где настройки и логи?',
      'faq.a3': 'Профиль и логи лежат в %APPDATA%\\Nexora. Папку можно открыть прямо из настроек.',
      'faq.q4': 'Почему Windows предупреждает?',
      'faq.a4': 'Установщик не подписан сертификатом. Скачивай его только из официального репозитория Nexora.',
      'faq.q5': 'Как обновить лаунчер?',
      'faq.a5': 'Скачай новый установщик и запусти его. Автоматического канала обновлений у лаунчера нет.',
      'footer.tagline': 'Лаунчер для Windows: каталог, загрузки и библиотека.',
      'footer.source': 'Исходный код',
      'footer.rights': '© 2026 Nexora'
    },
    en: {
      'meta.title': 'Nexora — a game launcher for Windows',
      'meta.description': 'Nexora is a Windows game launcher: catalog search, BitTorrent and HTTP downloads, file verification, and your own library.',
      'nav.features': 'Features',
      'nav.screens': 'Interface',
      'nav.download': 'Download',
      'nav.faq': 'FAQ',
      'nav.downloadCta': 'Download',
      'hero.kicker': 'A game launcher for Windows',
      'hero.title': 'Catalog, downloads, and a library in one launcher',
      'hero.lede': 'Nexora finds games in connected sources, downloads them over BitTorrent or HTTP with resume, and launches them from your library.',
      'hero.download': 'Download for Windows',
      'hero.more': 'What is inside',
      'hero.status': 'Version',
      'hero.shot': 'Home screen: catalog and featured picks',
      'fact.lang': 'interface languages',
      'fact.hash': 'file verification',
      'fact.torrent': 'torrent engine',
      'fact.os': '10 and 11, x64',
      'features.kicker': 'Features',
      'features.title': 'What Nexora handles',
      'features.lede': 'From search to the Play button, without unpacking archives or checking hashes by hand.',
      'f1.title': 'Search across sources',
      'f1.text': 'The built-in Rutor catalog and custom JSON sources: featured picks, filters, and live search.',
      'f2.title': 'BitTorrent inside',
      'f2.text': 'The rqbit engine written in Rust: magnet links, .torrent files, fast resume, and speed limits.',
      'f3.title': 'Resumable HTTP',
      'f3.text': 'Range requests, a staging folder, and an atomic install after verification.',
      'f4.title': 'SHA-256 verification',
      'f4.text': 'Hashes of the final files are checked after unpacking. A mismatch cancels the install.',
      'f5.title': 'Your own library',
      'f5.text': 'Install and update statuses, game launch, file checks, folder access, and removal.',
      'f6.title': 'Settings that fit',
      'f6.text': 'Library folder, speed limits, cache cleanup, interface language, and accent color.',
      'screens.kicker': 'Interface',
      'screens.title': 'One launcher for the whole path',
      'screens.lede': 'Catalog, library, game page, and settings share one dark interface.',
      'screen.library.title': 'Library',
      'screen.library.text': 'Installed games and quick launch.',
      'screen.details.title': 'Game page',
      'screen.details.text': 'Size, version, specs, and storage.',
      'screen.settings.title': 'Settings',
      'screen.settings.text': 'Folders, speed, language, and accent.',
      'flow.kicker': 'How it works',
      'flow.title': 'Three steps to play',
      'flow.lede': 'Nexora carries you from search to launch and keeps download progress between sessions.',
      'step1.title': 'Find a game',
      'step1.text': 'Type a name in search or open a featured pick in the catalog.',
      'step2.title': 'Download',
      'step2.text': 'Nexora picks a source and shows speed, progress, and time left.',
      'step3.title': 'Play',
      'step3.text': 'Verify the files and launch the game from your library.',
      'download.kicker': 'Download',
      'download.title': 'Download Nexora',
      'download.lede': 'Windows x64 installer. .NET is included.',
      'download.current': 'Current build',
      'download.ready': 'Ready to install',
      'download.size': 'Size',
      'download.updated': 'File modified',
      'download.file': 'File',
      'download.button': 'Download installer',
      'download.external': 'The latest GitHub release page will open.',
      'download.unsigned': 'The installer is not digitally signed, so Windows may show a publisher warning.',
      'download.before': 'Before installing',
      'download.req1': 'Windows 10 build 18362+ or Windows 11, x64',
      'download.req2': 'Internet access for search and downloads',
      'download.req3': 'Disk space for the game library',
      'download.req4': '.NET 8 is not required: the build is self-contained',
      'download.releases': 'All releases on GitHub',
      'faq.kicker': 'Questions',
      'faq.title': 'Before your first launch',
      'faq.lede': 'The short version on requirements, folders, and updates.',
      'faq.q1': 'Do I need to install .NET?',
      'faq.a1': 'No. Nexora is a self-contained app and carries the required components with it.',
      'faq.q2': 'Where do games install?',
      'faq.a2': 'By default to %USERPROFILE%\\Games\\Nexora. You can pick the folder during setup and change it in settings.',
      'faq.q3': 'Where are settings and logs?',
      'faq.a3': 'The profile and logs live in %APPDATA%\\Nexora. The folder opens from settings.',
      'faq.q4': 'Why does Windows warn me?',
      'faq.a4': 'The installer is not signed. Download it only from the official Nexora repository.',
      'faq.q5': 'How do I update the launcher?',
      'faq.a5': 'Download the new installer and run it. The launcher has no automatic update channel.',
      'footer.tagline': 'A Windows launcher: catalog, downloads, and library.',
      'footer.source': 'Source code',
      'footer.rights': '© 2026 Nexora'
    },
    uk: {
      'meta.title': 'Nexora — ігровий лаунчер для Windows',
      'meta.description': 'Nexora — лаунчер для Windows: пошук ігор у каталозі, завантаження через BitTorrent і HTTP, перевірка файлів і власна бібліотека.',
      'nav.features': 'Можливості',
      'nav.screens': 'Інтерфейс',
      'nav.download': 'Завантаження',
      'nav.faq': 'Питання',
      'nav.downloadCta': 'Завантажити',
      'hero.kicker': 'Ігровий лаунчер для Windows',
      'hero.title': 'Каталог, завантаження та бібліотека в одному лаунчері',
      'hero.lede': 'Nexora шукає ігри в підключених джерелах, завантажує їх через BitTorrent або HTTP з докачуванням і запускає з вашої бібліотеки.',
      'hero.download': 'Завантажити для Windows',
      'hero.more': 'Що всередині',
      'hero.status': 'Версія',
      'hero.shot': 'Головний екран: каталог і добірки',
      'fact.lang': 'мови інтерфейсу',
      'fact.hash': 'перевірка файлів',
      'fact.torrent': 'торрент-рушій',
      'fact.os': '10 і 11, x64',
      'features.kicker': 'Можливості',
      'features.title': 'Що Nexora робить сама',
      'features.lede': 'Від пошуку до кнопки «Грати» — без ручного розпакування архівів і перевірки хешів.',
      'f1.title': 'Пошук за джерелами',
      'f1.text': 'Вбудований каталог Rutor і власні JSON-джерела: добірки, фільтри й пошук у реальному часі.',
      'f2.title': 'BitTorrent всередині',
      'f2.text': 'Рушій rqbit на Rust: magnet-посилання, .torrent, fast resume і обмеження швидкості.',
      'f3.title': 'HTTP з докачуванням',
      'f3.text': 'Range-запити, тимчасова папка staging і атомарне встановлення після перевірки.',
      'f4.title': 'Перевірка SHA-256',
      'f4.text': 'Хеші фінальних файлів звіряються після розпакування. Не збіглося — встановлення скасовується.',
      'f5.title': 'Власна бібліотека',
      'f5.text': 'Статуси встановлення й оновлень, запуск гри, перевірка файлів, папка та видалення.',
      'f6.title': 'Налаштування під себе',
      'f6.text': 'Папка бібліотеки, обмеження швидкості, очищення кешу, мова інтерфейсу й колір акценту.',
      'screens.kicker': 'Інтерфейс',
      'screens.title': 'Один лаунчер на весь шлях',
      'screens.lede': 'Каталог, бібліотека, сторінка гри та налаштування зібрані в одному темному інтерфейсі.',
      'screen.library.title': 'Бібліотека',
      'screen.library.text': 'Встановлені ігри та швидкий запуск.',
      'screen.details.title': 'Сторінка гри',
      'screen.details.text': 'Розмір, версія, характеристики та сховище.',
      'screen.settings.title': 'Налаштування',
      'screen.settings.text': 'Папки, швидкість, мова й акцент.',
      'flow.kicker': 'Як це працює',
      'flow.title': 'Три кроки до гри',
      'flow.lede': 'Nexora веде від пошуку до запуску й зберігає прогрес завантаження між сеансами.',
      'step1.title': 'Знайди гру',
      'step1.text': 'Введи назву в пошук або відкрий добірку в каталозі.',
      'step2.title': 'Завантаж',
      'step2.text': 'Nexora вибере джерело й покаже швидкість, прогрес і залишок.',
      'step3.title': 'Грай',
      'step3.text': 'Перевір цілісність файлів і відкрий гру з бібліотеки.',
      'download.kicker': 'Завантаження',
      'download.title': 'Завантажити Nexora',
      'download.lede': 'Інсталятор для Windows x64. .NET ставити окремо не потрібно.',
      'download.current': 'Поточна збірка',
      'download.ready': 'Готово до встановлення',
      'download.size': 'Розмір',
      'download.updated': 'Файл змінено',
      'download.file': 'Файл',
      'download.button': 'Завантажити інсталятор',
      'download.external': 'Відкриється сторінка останнього релізу на GitHub.',
      'download.unsigned': 'Інсталятор не підписано цифровим сертифікатом, тому Windows може показати попередження про видавця.',
      'download.before': 'Перед встановленням',
      'download.req1': 'Windows 10 build 18362+ або Windows 11, x64',
      'download.req2': 'Інтернет для пошуку й завантажень',
      'download.req3': 'Місце на диску для бібліотеки ігор',
      'download.req4': '.NET 8 не потрібен: збірка self-contained',
      'download.releases': 'Усі релізи на GitHub',
      'faq.kicker': 'Питання',
      'faq.title': 'Перед першим запуском',
      'faq.lede': 'Коротко про вимоги, папки та оновлення.',
      'faq.q1': 'Чи потрібно встановлювати .NET?',
      'faq.a1': 'Ні. Nexora збирається як self-contained застосунок і несе потрібні компоненти з собою.',
      'faq.q2': 'Куди встановлюються ігри?',
      'faq.a2': 'За замовчуванням у %USERPROFILE%\\Games\\Nexora. Папку можна вибрати під час встановлення і змінити в налаштуваннях.',
      'faq.q3': 'Де налаштування й логи?',
      'faq.a3': 'Профіль і логи лежать у %APPDATA%\\Nexora. Папку можна відкрити з налаштувань.',
      'faq.q4': 'Чому Windows попереджає?',
      'faq.a4': 'Інсталятор не підписано сертифікатом. Завантажуйте його лише з офіційного репозиторію Nexora.',
      'faq.q5': 'Як оновити лаунчер?',
      'faq.a5': 'Завантаж новий інсталятор і запусти його. Автоматичного каналу оновлень у лаунчера немає.',
      'footer.tagline': 'Лаунчер для Windows: каталог, завантаження та бібліотека.',
      'footer.source': 'Вихідний код',
      'footer.rights': '© 2026 Nexora'
    }
  };

  const localeCodes = { ru: 'ru-RU', en: 'en-US', uk: 'uk-UA' };
  const supported = Object.keys(translations);
  const languageSelect = document.querySelector('[data-language]');
  const metaDescription = document.querySelector('meta[name="description"]');

  const preferredLanguage = () => {
    const stored = localStorage.getItem('nexora-language');
    return stored && supported.includes(stored) ? stored : 'ru';
  };

  let currentLanguage = preferredLanguage();

  const applyLanguage = (lang) => {
    const dictionary = translations[lang] || translations.ru;
    currentLanguage = lang;

    document.querySelectorAll('[data-i18n]').forEach((element) => {
      const value = dictionary[element.dataset.i18n];
      if (value) element.textContent = value;
    });

    document.title = dictionary['meta.title'];
    if (metaDescription) metaDescription.setAttribute('content', dictionary['meta.description']);
    document.documentElement.lang = lang;
    if (languageSelect) languageSelect.value = lang;
    localStorage.setItem('nexora-language', lang);
    formatRelease(currentRelease, true);
  };

  if (languageSelect) {
    languageSelect.addEventListener('change', () => {
      applyLanguage(supported.includes(languageSelect.value) ? languageSelect.value : 'ru');
    });
  }

  const t = (key) => (translations[currentLanguage] || translations.ru)[key] || key;

  // Mobile navigation

  const menuButton = document.querySelector('.menu-toggle');
  const navigation = document.querySelector('.main-nav');

  if (menuButton && navigation) {
    const closeMenu = () => {
      navigation.classList.remove('open');
      menuButton.setAttribute('aria-expanded', 'false');
    };

    menuButton.addEventListener('click', () => {
      const open = navigation.classList.toggle('open');
      menuButton.setAttribute('aria-expanded', String(open));
    });

    navigation.querySelectorAll('a').forEach((link) => link.addEventListener('click', closeMenu));
    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') closeMenu();
    });
    document.addEventListener('click', (event) => {
      if (!navigation.contains(event.target) && !menuButton.contains(event.target)) closeMenu();
    });
  }

  // Release manifest

  const formatSize = (bytes) => {
    if (!Number.isFinite(bytes) || bytes <= 0) return '—';
    return `${(bytes / (1024 * 1024)).toFixed(1)} MiB`;
  };

  const formatFileTime = (value) => {
    if (!value) return '—';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '—';
    return `${new Intl.DateTimeFormat(localeCodes[currentLanguage] || 'ru-RU', {
      day: '2-digit',
      month: 'short',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      timeZone: 'UTC'
    }).format(date)} UTC`;
  };

  let currentRelease = null;

  const formatRelease = (release, redrawOnly = false) => {
    if (!release) return;
    currentRelease = release;

    document.querySelectorAll('[data-version]').forEach((element) => {
      element.textContent = release.version || '—';
    });
    document.querySelectorAll('[data-size]').forEach((element) => {
      element.textContent = formatSize(release.sizeBytes);
    });
    document.querySelectorAll('[data-file-date]').forEach((element) => {
      element.textContent = formatFileTime(release.fileModifiedUtc);
    });
    document.querySelectorAll('[data-filename]').forEach((element) => {
      element.textContent = release.filename || '—';
    });

    if (redrawOnly) return;

    const external = typeof release.installer === 'string' && /^https?:/i.test(release.installer);
    document.querySelectorAll('[data-installer]').forEach((link) => {
      if (release.installer) link.href = release.installer;
    });
    if (external) {
      const status = document.querySelector('[data-download-status]');
      if (status) status.textContent = t('download.external');
    }
  };

  fetch('releases.json', { cache: 'no-cache' })
    .then((response) => {
      if (!response.ok) throw new Error(`Release data unavailable (${response.status})`);
      return response.json();
    })
    .then((data) => {
      if (data && data.current) formatRelease(data.current);
    })
    .catch(() => {
      const status = document.querySelector('[data-download-status]');
      if (status) status.textContent = '—';
    });

  // GitHub links

  const detectRepository = () => {
    const configured = document.querySelector('meta[name="nexora-repo"]')?.getAttribute('content')?.trim();
    if (configured) return configured.replace(/^https?:\/\/github\.com\//i, '').replace(/\/+$/, '');

    const host = window.location.hostname;
    if (!host.endsWith('.github.io')) return '';
    const owner = host.slice(0, -'.github.io'.length);
    const repo = window.location.pathname.split('/').filter(Boolean)[0] || '';
    return repo ? `${owner}/${repo}` : owner;
  };

  const repository = detectRepository();
  if (repository) {
    const base = `https://github.com/${repository}`;
    document.querySelectorAll('[data-repo-link]').forEach((link) => {
      link.href = base;
      link.hidden = false;
    });
    document.querySelectorAll('[data-releases-link]').forEach((link) => {
      link.href = `${base}/releases`;
      link.hidden = false;
    });
  }

  applyLanguage(currentLanguage);
})();

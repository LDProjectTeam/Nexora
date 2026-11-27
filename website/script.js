(() => {
  const locale = window.VellumLocale || { lang: 'ru', translate: (value) => value };
  const t = locale.translate;
  const intlLocale = locale.lang === 'en' ? 'en-US' : locale.lang === 'ua' ? 'uk-UA' : 'ru-RU';
  const menuButton = document.querySelector('.menu-toggle');
  const navigation = document.querySelector('.main-nav');
  const DEFAULT_DOWNLOAD_VERSION = '1.0.73';
  let releaseData = null;
  let pendingDownloadDialog = false;
  let downloadDialog = null;
  let downloadReturnFocus = null;

  if (menuButton && navigation) {
    const closeMenu = () => {
      navigation.classList.remove('open');
      menuButton.setAttribute('aria-expanded', 'false');
    };

    menuButton.addEventListener('click', () => {
      const isOpen = navigation.classList.toggle('open');
      menuButton.setAttribute('aria-expanded', String(isOpen));
    });
    navigation.querySelectorAll('a').forEach((link) => link.addEventListener('click', closeMenu));
    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') closeMenu();
    });
  }

  document.querySelectorAll('.language-control').forEach((picker) => {
    const select = picker.querySelector('[data-language]');
    const trigger = picker.querySelector('[data-language-trigger]');
    const menu = picker.querySelector('.language-menu');
    const options = [...picker.querySelectorAll('[data-language-option]')];
    const currentLabel = picker.querySelector('[data-language-current]');
    if (!select || !trigger || !menu || options.length === 0) return;

    const syncSelection = () => {
      const selected = select.value;
      const selectedOption = options.find((option) => option.dataset.languageOption === selected);
      if (currentLabel) currentLabel.textContent = selected.toUpperCase();
      trigger.setAttribute('aria-label', `${t('Choose language')}: ${selectedOption?.querySelector('span')?.textContent || selected.toUpperCase()}`);
      options.forEach((option) => option.setAttribute('aria-checked', String(option.dataset.languageOption === selected)));
    };
    const closeMenu = (restoreFocus = false) => {
      menu.hidden = true;
      trigger.setAttribute('aria-expanded', 'false');
      if (restoreFocus) trigger.focus();
    };
    const openMenu = () => {
      menu.hidden = false;
      trigger.setAttribute('aria-expanded', 'true');
      options.find((option) => option.dataset.languageOption === select.value)?.focus();
    };

    syncSelection();
    trigger.addEventListener('click', () => {
      if (menu.hidden) openMenu();
      else closeMenu();
    });
    options.forEach((option) => option.addEventListener('click', () => {
      if (select.value === option.dataset.languageOption) {
        closeMenu(true);
        return;
      }
      select.value = option.dataset.languageOption;
      select.dispatchEvent(new Event('change', { bubbles: true }));
    }));
    select.addEventListener('change', syncSelection);
    menu.addEventListener('keydown', (event) => {
      const currentIndex = options.indexOf(document.activeElement);
      let nextIndex = currentIndex;
      if (event.key === 'ArrowDown') nextIndex = (currentIndex + 1) % options.length;
      else if (event.key === 'ArrowUp') nextIndex = (currentIndex - 1 + options.length) % options.length;
      else if (event.key === 'Home') nextIndex = 0;
      else if (event.key === 'End') nextIndex = options.length - 1;
      else if (event.key === 'Escape') {
        event.preventDefault();
        closeMenu(true);
        return;
      } else return;
      event.preventDefault();
      options[nextIndex].focus();
    });
    document.addEventListener('click', (event) => {
      if (!picker.contains(event.target)) closeMenu();
    });
  });

  const downloadIcons = {
    installer: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3v11m0 0 4-4m-4 4-4-4M5 20h14"/></svg>',
    msi: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7.5 12 3l8 4.5v9L12 21l-8-4.5v-9Z"/><path d="m4 7.5 8 4.5 8-4.5M12 12v9"/></svg>',
    portable: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3h10v18H7z"/><path d="M12 3v4M12 10v3M12 16v2"/></svg>',
    migrator: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h13m0 0-3-3m3 3-3 3M20 17H7m0 0 3-3m-3 3 3 3"/></svg>',
    arrow: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14m0 0 6-6m-6 6-6-6"/></svg>'
  };

  const safeDownloadUrl = (value) => {
    if (!value) return null;
    try {
      const resolved = new URL(value, location.href);
      return resolved.protocol === 'http:' || resolved.protocol === 'https:' ? resolved.href : null;
    } catch {
      return null;
    }
  };

  const buildDownloadOptions = (data) => {
    const current = data?.current || {};
    const version = current.version || DEFAULT_DOWNLOAD_VERSION;
    const installerUrl = current.installer || `downloads/Vellum-Setup-${version}-win-x64.exe`;
    const installerName = current.filename || installerUrl.split('/').pop() || `Vellum-Setup-${version}-win-x64.exe`;
    const base = installerUrl.includes('/') ? installerUrl.slice(0, installerUrl.lastIndexOf('/') + 1) : '';
    const declared = current.downloads || {};
    const resolve = (key, fallbackName, fallbackSize) => {
      const entry = declared[key] || {};
      const filename = entry.filename || fallbackName;
      return {
        filename,
        url: entry.url || `${base}${filename}`,
        sizeBytes: typeof entry.sizeBytes === 'number' ? entry.sizeBytes : fallbackSize
      };
    };

    return {
      version,
      installer: resolve('installer', installerName, current.sizeBytes),
      msi: resolve('msi', `Vellum-${version}-win-x64.msi`, null),
      portable: resolve('portable', `Vellum-Portable-${version}-win-x64.zip`, null),
      migrator: resolve('migrator', 'Vellum-Migrator.exe', null),
      checksums: resolve('checksums', 'SHA256SUMS.txt', null)
    };
  };

  const ensureDownloadDialog = () => {
    if (downloadDialog) return downloadDialog;

    const root = document.createElement('div');
    root.className = 'download-dialog';
    root.hidden = true;
    root.innerHTML = `
      <div class="download-dialog-backdrop" data-download-dismiss></div>
      <section class="download-dialog-panel" role="dialog" aria-modal="true" aria-labelledby="download-dialog-title" tabindex="-1">
        <header class="download-dialog-head">
          <div><p class="eyebrow" data-download-eyebrow></p><h2 id="download-dialog-title"></h2></div>
          <button class="download-dialog-close" type="button" data-download-dismiss aria-label="">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18"/></svg>
          </button>
        </header>
        <p class="download-dialog-lede" data-download-lede></p>
        <div class="download-options" data-download-options></div>
        <p class="download-dialog-footnote" data-download-footnote></p>
        <a class="download-checksum" data-download-checksum hidden></a>
      </section>`;
    document.body.append(root);

    const panel = root.querySelector('.download-dialog-panel');
    const closeButton = root.querySelector('.download-dialog-close');
    closeButton.setAttribute('aria-label', t('Закрыть'));
    root.querySelector('[data-download-eyebrow]').textContent = t('Выберите вариант загрузки');
    root.querySelector('[data-download-lede]').textContent = t('EXE, MSI или портативная сборка — выберите, что скачать.');
    root.querySelector('[data-download-footnote]').textContent = t('Для Windows 10 build 18362+ и Windows 11 · x64');

    root.addEventListener('click', (event) => {
      if (event.target instanceof Element && event.target.closest('[data-download-dismiss]'))
        closeDownloadDialog();
    });

    document.addEventListener('keydown', (event) => {
      if (root.hidden) return;
      if (event.key === 'Escape') {
        event.preventDefault();
        closeDownloadDialog();
        return;
      }
      if (event.key !== 'Tab') return;
      const focusable = [...panel.querySelectorAll('a[href], button:not([disabled])')]
        .filter((element) => !element.hidden && element.getClientRects().length > 0);
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (document.activeElement === panel) {
        event.preventDefault();
        (event.shiftKey ? last : first).focus();
      } else if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    });

    downloadDialog = {
      root,
      panel,
      title: root.querySelector('#download-dialog-title'),
      options: root.querySelector('[data-download-options]'),
      checksum: root.querySelector('[data-download-checksum]')
    };
    return downloadDialog;
  };

  const renderDownloadOptions = () => {
    const dialog = ensureDownloadDialog();
    const downloads = buildDownloadOptions(releaseData);
    dialog.title.textContent = `${t('Скачать Vellum')} ${downloads.version}`;
    dialog.options.replaceChildren();
    dialog.checksum.hidden = true;

    if (!releaseData) {
      const message = document.createElement('p');
      if (pendingDownloadDialog) {
        message.className = 'download-options-loading';
        message.textContent = t('Загрузка вариантов…');
      } else {
        message.className = 'download-options-error';
        message.append(`${t('Не удалось загрузить список файлов. Откройте страницу релизов.')} `);
        const fallback = document.createElement('a');
        fallback.href = 'https://github.com/LDProjectTeam/Vellum/releases/latest';
        fallback.target = '_blank';
        fallback.rel = 'noopener';
        fallback.textContent = t('Открыть релизы на GitHub');
        message.append(fallback);
      }
      dialog.options.append(message);
      return;
    }

    const specs = [
      { key: 'installer', title: 'Установщик (EXE)', note: 'Обычная установка в профиль пользователя', badge: 'Рекомендуется' },
      { key: 'msi', title: 'MSI-пакет', note: 'Установка через Windows Installer' },
      { key: 'portable', title: 'Портативная версия (ZIP)', note: 'Распаковать и запустить Vellum.exe' },
      { key: 'migrator', title: 'Мигратор с Nexora', note: 'Перенос профиля и библиотеки' }
    ];

    specs.forEach((spec) => {
      const item = downloads[spec.key];
      const url = safeDownloadUrl(item?.url);
      if (!url) return;

      const link = document.createElement('a');
      link.className = `download-option${spec.key === 'installer' ? ' is-primary' : ''}`;
      link.href = url;
      if (item.filename) link.setAttribute('download', item.filename);

      const icon = document.createElement('span');
      icon.className = 'download-option-icon';
      icon.innerHTML = downloadIcons[spec.key];

      const body = document.createElement('span');
      body.className = 'download-option-body';

      const title = document.createElement('span');
      title.className = 'download-option-title';
      title.append(document.createTextNode(t(spec.title)));
      if (spec.badge) {
        const badge = document.createElement('span');
        badge.className = 'download-option-badge';
        badge.textContent = t(spec.badge);
        title.append(badge);
      }

      const note = document.createElement('span');
      note.className = 'download-option-note';
      note.textContent = t(spec.note);

      const file = document.createElement('span');
      file.className = 'download-option-file';
      file.textContent = item.filename || '';

      body.append(title, note, file);

      const size = document.createElement('span');
      size.className = 'download-option-size';
      size.textContent = formatSize(item.sizeBytes);

      const arrow = document.createElement('span');
      arrow.className = 'download-option-arrow';
      arrow.innerHTML = downloadIcons.arrow;

      link.append(icon, body, size, arrow);
      link.addEventListener('click', () => window.setTimeout(closeDownloadDialog, 40));
      dialog.options.append(link);
    });

    const checksumUrl = safeDownloadUrl(downloads.checksums?.url);
    if (checksumUrl) {
      dialog.checksum.href = checksumUrl;
      dialog.checksum.textContent = `${t('Контрольные суммы SHA-256')} · ${downloads.checksums.filename || 'SHA256SUMS.txt'}`;
      dialog.checksum.hidden = false;
    }
  };

  const openDownloadDialog = (trigger) => {
    const dialog = ensureDownloadDialog();
    downloadReturnFocus = trigger || document.activeElement;
    pendingDownloadDialog = !releaseData;
    renderDownloadOptions();
    dialog.root.hidden = false;
    document.body.classList.add('download-dialog-open');
    window.requestAnimationFrame(() => dialog.panel.focus());
  };

  const closeDownloadDialog = () => {
    if (!downloadDialog || downloadDialog.root.hidden) return;
    downloadDialog.root.hidden = true;
    document.body.classList.remove('download-dialog-open');
    pendingDownloadDialog = false;
    if (downloadReturnFocus && typeof downloadReturnFocus.focus === 'function')
      downloadReturnFocus.focus();
  };

  const wireDownloadTriggers = () => {
    document.querySelectorAll('[data-download], [data-installer]').forEach((trigger) => {
      trigger.addEventListener('click', (event) => {
        if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey)
          return;
        event.preventDefault();
        openDownloadDialog(trigger);
      });
    });
  };

  const formatSize = (bytes) => {
    const value = Number(bytes);
    return Number.isFinite(value) && value > 0 ? `${(value / (1024 * 1024)).toFixed(1)} MiB` : '';
  };

  const formatFileTime = (value) => {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '—';
    const formatted = new Intl.DateTimeFormat(intlLocale, {
      day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', timeZone: 'UTC'
    }).format(date);
    return `${formatted} UTC`;
  };

  const loadReleaseData = async () => {
    const response = await fetch('releases.json', { cache: 'no-cache' });
    if (!response.ok) throw new Error(`Release data unavailable (${response.status})`);
    return response.json();
  };

  loadReleaseData().then((data) => {
    const current = data.current;
    if (!current) return;

    releaseData = data;
    if (pendingDownloadDialog && downloadDialog && !downloadDialog.root.hidden) {
      pendingDownloadDialog = false;
      renderDownloadOptions();
    }

    document.querySelectorAll('[data-version]').forEach((element) => {
      element.textContent = current.version;
    });
    document.querySelectorAll('[data-download]').forEach((link) => {
      link.href = current.installer;
    });
    const installerLink = document.querySelector('[data-installer]');
    if (installerLink) installerLink.href = current.installer;

    document.querySelectorAll('[data-size]').forEach((element) => {
      element.textContent = formatSize(current.sizeBytes);
    });
    document.querySelectorAll('[data-file-date]').forEach((element) => {
      element.textContent = formatFileTime(current.fileModifiedUtc);
    });
    document.querySelectorAll('[data-release-date]').forEach((element) => {
      element.textContent = t(current.releaseDate || 'Не указана в проекте');
    });
    document.querySelectorAll('[data-filename]').forEach((element) => {
      element.textContent = current.filename;
    });

    const history = document.querySelector('[data-history]');
    if (!history) return;
    history.replaceChildren();

    (data.history || []).forEach((entry, index) => {
      const row = document.createElement('div');
      row.className = index === 0 ? 'history-row current' : 'history-row';
      row.setAttribute('role', 'row');

      const version = document.createElement('strong');
      version.setAttribute('role', 'cell');
      version.textContent = entry.version;
      const filename = document.createElement('span');
      filename.setAttribute('role', 'cell');
      filename.textContent = entry.filename;
      const fileTime = document.createElement('span');
      fileTime.setAttribute('role', 'cell');
      fileTime.textContent = formatFileTime(entry.fileModifiedUtc);
      const size = document.createElement('span');
      size.setAttribute('role', 'cell');
      size.textContent = formatSize(entry.sizeBytes);
      row.append(version, filename, fileTime, size);
      history.append(row);
    });

    const count = document.querySelector('[data-history-count]');
    if (count) {
      const countText = locale.lang === 'en' ? 'installer files in the project archive' : locale.lang === 'ua' ? 'файлів інсталяторів в архіві проєкту' : 'файлов установщиков в рабочем архиве';
      count.textContent = `${new Intl.NumberFormat(intlLocale).format(data.historyCount)} ${countText}`;
    }
  }).catch(() => {
    releaseData = null;
    if (downloadDialog && !downloadDialog.root.hidden) {
      pendingDownloadDialog = false;
      renderDownloadOptions();
    }
    const history = document.querySelector('[data-history]');
    if (history) {
      const message = document.createElement('p');
      message.className = 'history-empty';
      message.textContent = t('Не удалось загрузить список сборок. Проверь подключение и обнови страницу.');
      history.replaceChildren(message);
    }
    const status = document.querySelector('[data-download-status]');
    if (status) status.textContent = t('Не удалось загрузить сведения о файле. Попробуй обновить страницу.');
  });

  const copyButton = document.querySelector('[data-copy-path]');
  if (copyButton) {
    copyButton.addEventListener('click', async () => {
      const status = document.querySelector('[data-copy-status]');
      try {
        await navigator.clipboard.writeText('%APPDATA%\\Vellum');
        status.textContent = t('Путь скопирован');
      } catch {
        status.textContent = t('Скопируй путь вручную: %APPDATA%\\Vellum');
      }
    });
  }

  wireDownloadTriggers();

  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  if (!reduceMotion) document.documentElement.classList.add('motion-enabled');
  if (!reduceMotion && 'IntersectionObserver' in window) {
    const sections = document.querySelectorAll('main > section:not(.hero)');
    const observer = new IntersectionObserver((entries, activeObserver) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.classList.add('is-visible');
          activeObserver.unobserve(entry.target);
        }
      });
    }, { rootMargin: '0px 0px -32px 0px', threshold: 0.08 });

    sections.forEach((section) => {
      section.classList.add('reveal');
      observer.observe(section);
    });
  }
})();

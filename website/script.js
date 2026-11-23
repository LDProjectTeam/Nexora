(() => {
  const locale = window.VellumLocale || { lang: 'ru', translate: (value) => value };
  const t = locale.translate;
  const intlLocale = locale.lang === 'en' ? 'en-US' : locale.lang === 'ua' ? 'uk-UA' : 'ru-RU';
  const menuButton = document.querySelector('.menu-toggle');
  const navigation = document.querySelector('.main-nav');

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

  const formatSize = (bytes) => `${(bytes / (1024 * 1024)).toFixed(1)} MiB`;
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

export function scrollToBottom(selector) {
    const element = document.querySelector(selector);
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
}

export function registerLogScrollListener(selector, dotNetHelper) {
    const element = document.querySelector(selector);
    if (!element) return;
    element.addEventListener('scroll', () => {
        // If scrolled to top (within 10px), trigger load previous
        if (element.scrollTop < 10) {
            dotNetHelper.invokeMethodAsync('OnScrollToTop');
        }
    });
}

export function preserveScroll(selector, previousScrollHeight) {
    const element = document.querySelector(selector);
    if (!element) return;
    let currentHeight = element.scrollHeight;
    element.scrollTop = currentHeight - previousScrollHeight;
}

export function getScrollHeight(selector) {
    const element = document.querySelector(selector);
    return element ? element.scrollHeight : 0;
}

export function setElementHtml(elementOrSelector, html, attachCopy = false) {
    let element = null;
    if (typeof elementOrSelector === 'string') {
        element = document.querySelector(elementOrSelector);
    } else if (elementOrSelector instanceof HTMLElement) {
        element = elementOrSelector;
    }
    if (!element) return;

    element.innerHTML = html || '';

    if (attachCopy) {
        attachCopyButtons(element);
    }
}

export function attachCopyButtons(containerSelectorOrElement) {
    let container = document;
    let selector = '.md-content pre';
    if (typeof containerSelectorOrElement === 'string') {
        selector = `${containerSelectorOrElement} pre`;
    } else if (containerSelectorOrElement instanceof HTMLElement) {
        container = containerSelectorOrElement;
        selector = 'pre';
    }
    container.querySelectorAll(selector).forEach(pre => {
        if (pre.parentNode && pre.parentNode.classList && pre.parentNode.classList.contains('code-wrapper')) return;
        
        const wrapper = document.createElement('div');
        wrapper.className = 'code-wrapper';
        wrapper.style.position = 'relative';
        
        pre.parentNode.insertBefore(wrapper, pre);
        wrapper.appendChild(pre);
        
        const btn = document.createElement('button');
        btn.className = 'copy-btn';
        btn.innerHTML = '<span class="material-symbols-outlined" style="font-size: 16px;">content_copy</span>';
        
        // Styling
        btn.style.position = 'absolute';
        btn.style.top = '0.5rem';
        btn.style.right = '0.5rem';
        btn.style.background = 'rgba(255,255,255,0.1)';
        btn.style.border = 'none';
        btn.style.color = '#e2e8f0';
        btn.style.borderRadius = '4px';
        btn.style.padding = '4px 6px';
        btn.style.cursor = 'pointer';
        btn.style.opacity = '0';
        btn.style.transition = 'opacity 0.2s, background 0.2s';
        
        wrapper.onmouseenter = () => btn.style.opacity = '1';
        wrapper.onmouseleave = () => btn.style.opacity = '0';
        
        btn.onmouseover = () => btn.style.background = 'rgba(255,255,255,0.2)';
        btn.onmouseout = () => btn.style.background = 'rgba(255,255,255,0.1)';
        
        btn.onclick = () => {
            const codeBlock = pre.querySelector('code');
            const textToCopy = codeBlock ? codeBlock.innerText : pre.innerText;
            
            navigator.clipboard.writeText(textToCopy.trim()).then(() => {
                btn.innerHTML = '<span class="material-symbols-outlined" style="font-size: 16px;">check</span>';
                setTimeout(() => {
                    btn.innerHTML = '<span class="material-symbols-outlined" style="font-size: 16px;">content_copy</span>';
                }, 2000);
            });
        };
        
        wrapper.appendChild(btn);
    });
}

export async function copyToClipboard(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            console.error('Failed to copy using navigator.clipboard:', e);
        }
    }
    try {
        const textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.style.position = 'fixed';
        textarea.style.opacity = '0';
        document.body.appendChild(textarea);
        textarea.focus();
        textarea.select();
        const successful = document.execCommand('copy');
        document.body.removeChild(textarea);
        return successful;
    } catch (err) {
        console.error('Fallback copy failed:', err);
        return false;
    }
}

export function initDocViewerLinkHandler(containerSelector, dotNetHelper) {
    const container = document.querySelector(containerSelector);
    if (!container) return;

    if (container._docLinkClickHandler) {
        container.removeEventListener('click', container._docLinkClickHandler);
    }

    const clickHandler = (e) => {
        const link = e.target.closest('a');
        if (!link) return;

        const rawHref = link.getAttribute('href');
        if (rawHref === null || rawHref === undefined) return;

        const href = rawHref.trim();
        if (href === '' || href === '#' || href.startsWith('javascript:')) {
            e.preventDefault();
            e.stopPropagation();
            return;
        }

        // 1. In-page anchor hash links (e.g. #overview, #architecture)
        if (href.startsWith('#')) {
            e.preventDefault();
            e.stopPropagation();
            const targetId = href.substring(1).toLowerCase();
            scrollToDocAnchor(containerSelector, targetId);
            return;
        }

        // 2. External links (http://, https://, mailto:)
        if (/^(https?:|\/\/|mailto:)/i.test(href)) {
            link.setAttribute('target', '_blank');
            link.setAttribute('rel', 'noopener noreferrer');
            return;
        }

        // 3. Document links (e.g. DOCUMENTATION.md, README.md, LICENSE, NOTICE, etc.)
        let cleanPath = href.trim();
        let anchor = '';
        const hashIdx = cleanPath.indexOf('#');
        if (hashIdx !== -1) {
            anchor = cleanPath.substring(hashIdx + 1);
            cleanPath = cleanPath.substring(0, hashIdx);
        }

        cleanPath = cleanPath.replace(/^(\.\/|\/|docs\/|\/docs\/)+/i, '');
        const baseName = (cleanPath.split('/').pop() || cleanPath).toLowerCase();

        const knownDocs = {
            'readme.md': 'readme',
            'readme': 'readme',
            'documentation.md': 'docs',
            'documentation': 'docs',
            'license': 'license',
            'license.md': 'license',
            'license.txt': 'license',
            'notice': 'notice',
            'notice.md': 'notice',
            'notice.txt': 'notice'
        };

        const matchedDocId = knownDocs[baseName];
        if (matchedDocId) {
            e.preventDefault();
            e.stopPropagation();
            if (dotNetHelper) {
                dotNetHelper.invokeMethodAsync('OnInternalDocLinkClicked', matchedDocId, anchor);
            }
            return;
        }

        // 4. Any other relative file links - prevent broken SPA navigation, open in new tab
        e.preventDefault();
        e.stopPropagation();
        window.open(href, '_blank', 'noopener,noreferrer');
    };

    container._docLinkClickHandler = clickHandler;
    container.addEventListener('click', clickHandler);
}

export function scrollToDocAnchor(containerSelector, targetId) {
    if (!targetId) return;
    const container = typeof containerSelector === 'string' 
        ? document.querySelector(containerSelector) 
        : containerSelector;
    if (!container) return;

    let target = null;
    try {
        target = container.querySelector('#' + CSS.escape(targetId));
    } catch (e) {}

    if (!target) {
        try {
            target = container.querySelector(`[id="${targetId}" i]`);
        } catch (e) {}
    }

    if (!target) {
        try {
            target = container.querySelector(`[name="${CSS.escape(targetId)}" i]`);
        } catch (e) {}
    }

    if (!target) {
        const headings = container.querySelectorAll('h1, h2, h3, h4, h5, h6');
        const normalizedTarget = targetId.replace(/[-_]/g, ' ').toLowerCase();
        for (const h of headings) {
            const hText = h.innerText.trim().toLowerCase();
            const hId = (h.id || '').toLowerCase();
            if (hId === targetId.toLowerCase() || hText === normalizedTarget || hText.startsWith(normalizedTarget)) {
                target = h;
                break;
            }
        }
    }

    if (target) {
        const containerRect = container.getBoundingClientRect();
        const targetRect = target.getBoundingClientRect();
        const relativeTop = targetRect.top - containerRect.top + container.scrollTop;
        container.scrollTo({
            top: Math.max(0, relativeTop - 20),
            behavior: 'smooth'
        });
    }
}

export function scrollSelectOptionIntoView(containerRef) {
    if (containerRef) {
        const highlighted = containerRef.querySelector('.highlighted');
        if (highlighted) {
            highlighted.scrollIntoView({ block: 'nearest' });
        }
    }
}

export async function downloadFileFromStream(fileName, contentStreamReference) {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer]);
    const url = URL.createObjectURL(blob);
    const anchorElement = document.createElement('a');
    anchorElement.href = url;
    anchorElement.download = fileName ?? '';
    anchorElement.click();
    anchorElement.remove();
    URL.revokeObjectURL(url);
}

export function setTheme(theme) {
    if (window.themeManager) {
        window.themeManager.setTheme(theme);
    } else {
        try {
            const validTheme = theme === 'dark' ? 'dark' : 'light';
            document.documentElement.setAttribute('data-theme', validTheme);
            localStorage.setItem('theme', validTheme);
            document.cookie = `theme=${validTheme};path=/;max-age=31536000;SameSite=Lax`;
        } catch (e) {
            console.error('Failed to set theme:', e);
        }
    }
}

export function getTheme() {
    if (window.themeManager) {
        return window.themeManager.getTheme();
    }
    try {
        const docTheme = document.documentElement.getAttribute('data-theme');
        if (docTheme === 'dark' || docTheme === 'light') return docTheme;
        const storedTheme = localStorage.getItem('theme');
        if (storedTheme === 'dark' || storedTheme === 'light') return storedTheme;
        const match = document.cookie.match(/(?:^|; )theme=(dark|light)/);
        if (match) return match[1];
    } catch (e) {
        console.error('Failed to get theme:', e);
    }
    return 'light';
}

export function initTheme() {
    if (window.themeManager) {
        window.themeManager.initTheme();
    } else {
        const theme = getTheme();
        setTheme(theme);
    }
}

let currentDropdownCleanup = null;

export function positionDropdown(inputElement, dropdownElement, dotNetHelper) {
    if (currentDropdownCleanup) currentDropdownCleanup();
    if (!inputElement || !dropdownElement) return;

    function updatePosition() {
        const rect = inputElement.getBoundingClientRect();
        dropdownElement.style.position = 'fixed';
        dropdownElement.style.top = rect.bottom + 4 + 'px';
        dropdownElement.style.left = rect.left + 'px';
        dropdownElement.style.width = rect.width + 'px';
        dropdownElement.style.zIndex = '9999';
    }

    updatePosition();

    const scrollHandler = (e) => {
        if (!e.target || !(e.target instanceof Node) || (e.target !== dropdownElement && !dropdownElement.contains(e.target))) {
            try { dotNetHelper.invokeMethodAsync('CloseDropdown'); } catch(e) {}
        }
    };

    window.addEventListener('scroll', scrollHandler, true);
    window.addEventListener('resize', scrollHandler, true);

    currentDropdownCleanup = () => {
        window.removeEventListener('scroll', scrollHandler, true);
        window.removeEventListener('resize', scrollHandler, true);
    };
}

export function cleanupDropdown() {
    if (currentDropdownCleanup) {
        currentDropdownCleanup();
        currentDropdownCleanup = null;
    }
}

// Auth storage helpers for CSP compliance
window.authStorage = {
    getAuthData: () => [
        localStorage.getItem('authToken'),
        localStorage.getItem('authRole'),
        localStorage.getItem('authUsername')
    ],
    clearAuthData: () => {
        localStorage.removeItem('authToken');
        localStorage.removeItem('authRole');
        localStorage.removeItem('authUsername');
    }
};


/**
 * Polyfill for WebView2 Drag and Drop Bug
 *
 * This polyfill patches a known bug in WebView2 where drag and drop functionalities are
 * not working as expected. This script provides a mock implementation to temporarily
 * overcome the issue.
 *
 * More information:
 * - https://github.com/MicrosoftEdge/WebView2Feedback/issues/2805
 * - https://github.com/dotnet/maui/issues/2205
 *
 *
 * Developers integrating this polyfill should implement their own feature detection or
 * settings toggle to decide when to apply this fix.
 *
 * Copyright (c) 2023 Iain Fraser
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy of
 * this software and associated documentation files (the "Software"), to deal in
 * the Software without restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
 * Software, and to permit persons to whom the Software is furnished to do so,
 * subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
 * INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
 * PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
 * OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
 * SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 *
 * Source: https://gist.github.com/ivanjx/b026ba331796e20a717778ae56760e3c
 * Vendored here unmodified; loaded only for the native Windows (WinUI/WebView2) shell —
 * see appInterop.enableWebView2DragPolyfillIfNeeded in app.js.
 */

(function (opts) {

let isDragging = false;
let draggedElement = null;
let startPosition = { x: 0, y: 0 };
let currentOverElement = null;
let ghostElement = null;
let dragStartEvt = null;

/**
 * Searches for the nearest ancestor of the provided element that has the draggable attribute set to true.
 * @param {HTMLElement} element - The starting element to begin the search from.
 * @returns {HTMLElement|null} - The draggable ancestor if found, otherwise null.
 */
function findDraggableAncestor(element) {
  while (element && element !== document.body) {
    if (element.getAttribute('draggable') === 'true') {
      return element;
    }
    element = element.parentElement;
  }
  return null;
}

/**
 * Creates a mock drag event.
 * @param {string} type - The type of the drag event (e.g., "dragstart", "dragend", etc.)
 * @param {Object} options - Options to be passed to the event.
 * @returns {Event} - A mocked drag event.
 */
function createMockDragEvent(type, options) {
  let event = new Event(type, options);
  event.dataTransfer = new DataTransfer();
  return event;
}

if (opts.setPointerCursor) {
  const styleTag = document.createElement('style');
  styleTag.textContent = '[draggable="true"] { cursor: pointer; }';

  const head = document.head;
  if (head.children.length > 0) {
    head.insertBefore(styleTag, head.children[0]);
  } else {
    head.appendChild(styleTag);
  }
}

document.addEventListener('mousedown', function (e) {
  if (e.button !== 0) return;

  let draggableElement = findDraggableAncestor(e.target);
  if (!draggableElement) return;

  draggableElement.setAttribute('draggable', 'false');

  isDragging = true;
  draggedElement = draggableElement;
  startPosition.x = e.clientX;
  startPosition.y = e.clientY;

  dragStartEvt = createMockDragEvent('dragstart', {
    bubbles: true,
    cancelable: true
  });
});

document.addEventListener('mousemove', function (e) {
  if (!isDragging) return;

  if (dragStartEvt !== null) {
    draggedElement.dispatchEvent(dragStartEvt);
    dragStartEvt = null;
  }

  if (!ghostElement) {
    ghostElement = draggedElement.cloneNode(true);
    applyStylesToGhost(draggedElement, ghostElement);
  }

  let x = e.clientX - startPosition.x;
  let y = e.clientY - startPosition.y;

  ghostElement.style.transform = `translate(${x}px, ${y}px)`;

  let elementBelow = document.elementFromPoint(e.clientX, e.clientY);

  if (elementBelow) {
    const dragOverEvt = createMockDragEvent("dragover", {
      bubbles: true,
      cancelable: true
    });

    dragOverEvt.clientX = e.clientX;
    dragOverEvt.clientY = e.clientY;
    dragOverEvt.offsetX = e.offsetX;
    dragOverEvt.offsetY = e.offsetY;

    elementBelow.dispatchEvent(dragOverEvt);

    if (elementBelow !== currentOverElement) {
      if (currentOverElement) {
        currentOverElement.dispatchEvent(
          createMockDragEvent("dragleave", {
            bubbles: true,
            cancelable: true
          })
        );
      }

      elementBelow.dispatchEvent(
        createMockDragEvent("dragenter", {
          bubbles: true,
          cancelable: true
        })
      );

      currentOverElement = elementBelow;
    }
  }
});

document.addEventListener('mouseup', function (e) {
  if (!isDragging || e.button !== 0) return;

  if (currentOverElement) {
    currentOverElement.dispatchEvent(createMockDragEvent('drop', {
      bubbles: true,
      cancelable: true
    }));
  }

  draggedElement.dispatchEvent(createMockDragEvent('dragend', {
    bubbles: true,
    cancelable: true
  }));

  isDragging = false;
  draggedElement.setAttribute('draggable', 'true');

  if (ghostElement) {
    document.body.removeChild(ghostElement);
  }

  ghostElement = null;
  draggedElement = null;
  dragStartEvt = null;
  currentOverElement = null;
});

/**
 * Applies computed styles from the original element to the ghost element.
 * @param {HTMLElement} original - The original draggable element.
 * @param {HTMLElement} ghost - The cloned "ghost" element.
 */
function applyStylesToGhost(original, ghost) {
  let computedStyles = window.getComputedStyle(original);
  for (let prop of computedStyles) {
    ghost.style[prop] = computedStyles[prop];
  }

  let rect = original.getBoundingClientRect();

  ghost.classList.remove('position-relative');
  ghost.classList.remove('position-absolute');
  ghost.classList.remove('position-static');
  ghost.classList.remove('position-sticky');

  ghost.style.position = 'fixed';
  ghost.style.left = `${rect.left}px`;
  ghost.style.top = `${rect.top}px`;
  ghost.style.zIndex = '1000';
  ghost.style.opacity = '0.7';
  ghost.style.pointerEvents = 'none';

  document.body.appendChild(ghost);
}

})({ setPointerCursor: true });

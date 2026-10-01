const chat = document.querySelector('#chat');
const form = document.querySelector('#composer');
const input = document.querySelector('#message');
const tokenInput = document.querySelector('#token');
const connect = document.querySelector('#connect');
const shell = document.querySelector('.shell');
const tokenKey = 'tolik.sessionToken';
const status = document.createElement('div');
status.className = 'module-status';
status.textContent = 'Core проверяется...';
shell.prepend(status);
tokenInput.value = sessionStorage.getItem(tokenKey) || '';

function add(text, kind) {
  const element = document.createElement('div');
  element.className = 'bubble ' + kind;
  element.textContent = text;
  chat.append(element);
  chat.scrollTop = chat.scrollHeight;
}

function authHeaders() {
  const token = sessionStorage.getItem(tokenKey);
  return token ? { Authorization: 'Bearer ' + token } : {};
}

async function loadStatus() {
  try {
    const response = await fetch('/status/modules', { headers: authHeaders() });
    if (!response.ok) throw new Error('status_failed');
    const data = await response.json();
    status.textContent = 'Core: ' + data.core + ' · Memory: ' + data.memory + ' · Voice: ' + data.voice;
  } catch {
    status.textContent = 'Core недоступен или сессия не подключена';
  }
}

connect.addEventListener('click', () => {
  const token = tokenInput.value.trim();
  if (token) sessionStorage.setItem(tokenKey, token);
  else sessionStorage.removeItem(tokenKey);
  loadStatus();
});

form.addEventListener('submit', async event => {
  event.preventDefault();
  const message = input.value.trim();
  if (!message) return;
  input.value = '';
  add(message, 'user');
  try {
    const response = await fetch('/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ message })
    });
    const data = await response.json();
    add(response.ok ? (data.answer || 'Ответ получен.') : ('Ошибка: ' + (data.detail || data.error || response.status)), 'assistant');
  } catch {
    add('Core API недоступен.', 'assistant');
  }
});

loadStatus();

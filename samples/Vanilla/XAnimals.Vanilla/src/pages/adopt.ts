import type { Page } from '@routeables/client';
import { routeables } from '../routeables';
import type { State } from '../routeables';
import { fill, h } from '../dom';
import type { View } from '../dom';

interface Pet { animal: { photoUrl: string }; name: string; location: string; glyph: string }
interface Choice { name: string; title: string; adoptCommand: unknown }

/** One button per animal: adopting it routes its card into the region "adopt". */
export function adopt(page: Page, target: string): View {
  let s = page.state as State;
  const choices = h('section', { class: 'about-group adopt-choices' });
  const status = h('p', { class: 'nav-status', role: 'status' });
  const cards = h('div', { class: 'adopt-zone', style: 'display: flex; flex-wrap: wrap; gap: 0.8rem' });
  const node = h('div', { class: 'about-page' },
    choices,
    h('section', { class: 'about-group adopt-bar' },
      status,
      h('button', { type: 'button', class: 'adopt-choice', onclick: () => void routeables.run(target, s.releaseCommand) }, 'Release the last'),
      h('button', { type: 'button', class: 'adopt-choice', onclick: () => void routeables.run(target, s.releaseAllCommand) }, 'Release all')),
    cards);

  const draw = (): void => {
    fill(choices, ((s.choices as Choice[] | undefined) ?? []).map(choice =>
      h('button', { type: 'button', class: 'adopt-choice', onclick: () => void routeables.run(target, choice.adoptCommand) }, choice.title)));
    status.textContent = s.status ?? '';
  };
  draw();

  // The cards are the history of the region of the adopted animals (`$pets`).
  const stop = routeables.follow('$pets', pets => fill(cards, ((pets?.state['pets'] as Pet[] | undefined) ?? []).map(pet =>
    h('article', { class: 'pet-card' },
      h('img', { src: pet.animal.photoUrl, alt: pet.name, width: 150, height: 190 }),
      h('strong', null, pet.name),
      h('span', { class: 'pet-location' }, pet.location),
      h('span', { class: 'pet-glyph' }, pet.glyph)))));

  return { node, update: next => { s = next.state as State; draw(); }, destroy: stop };
}

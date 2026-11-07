import { useEffect, useState } from 'react';
import { routeables, tabs } from '../routeables';
import type { PageProps } from '../hooks';
import { Region } from '../Region';

/** The items of a tab control are routes: each tab shows its route in the region of the page, and the region draws it. */
export function Tabs(_: PageProps) {
  const [selected, setSelected] = useState('');

  function select(route: string): void {
    setSelected(route);
    void routeables.goToIn('tabs-demo', route);
  }

  // A tab control selects its first item when it appears.
  useEffect(() => select(tabs[0]!.route), []);

  return (
    <>
      <div className="about-page">
        <div className="actions" role="tablist">
          {tabs.map(tab => (
            <button key={tab.route} type="button" role="tab" className="adopt-choice" aria-selected={tab.route === selected}
                    onClick={() => select(tab.route)}>{tab.header}</button>
          ))}
        </div>
      </div>
      <div className="about-page">
        <section className="about-group"><Region name="tabs-demo" /></section>
      </div>
    </>
  );
}

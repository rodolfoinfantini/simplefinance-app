// Sidebar menu for SimpleFinance: the entries, their icons and their labels.
//
// The static table below holds keys of the `menu` i18n namespace; the hook
// turns it into translated sections for the sidebar to render.

import { useMemo } from 'react'
import type { TFunction } from 'i18next'
import { useTranslation } from 'react-i18next'

export type NavLinkEntry = {
  kind: 'link'
  id: string
  to: string
  label: string
  /** lucide icon name, without the `lucide:` prefix */
  icon?: string
}

export type NavGroupEntry = {
  kind: 'group'
  id: string
  label: string
  icon: string
  links: NavLinkEntry[]
}

export type NavEntry = NavLinkEntry | NavGroupEntry

export type NavSection = {
  id: 'main' | 'settings'
  title: string
  entries: NavEntry[]
}

type LinkSpec = { kind: 'link'; id: string; to: string; labelKey: string; icon?: string }
type GroupSpec = { kind: 'group'; id: string; labelKey: string; icon: string; links: LinkSpec[] }
type SectionSpec = { id: NavSection['id']; titleKey: string; entries: Array<LinkSpec | GroupSpec> }

const link = (id: string, to: string, icon?: string): LinkSpec => ({ kind: 'link', id, to, labelKey: `items.${id}`, icon })

const MENU: readonly SectionSpec[] = [
  {
    id: 'main',
    titleKey: 'sections.main',
    entries: [
      link('dashboard', '/', 'layout-dashboard'),
      link('transactions', '/transactions', 'arrow-left-right'),
      link('accounts', '/accounts', 'landmark'),
      link('budget', '/budget', 'wallet'),
      link('investments', '/investments', 'trending-up'),
      link('netWorth', '/net-worth', 'scale'),
      link('recurring', '/recurring', 'repeat'),
      link('installments', '/installments', 'credit-card'),
      link('goals', '/goals', 'target'),
      link('reports', '/reports', 'file-text'),
      link('assistant', '/assistente', 'message-circle'),
    ],
  },
  {
    id: 'settings',
    titleKey: 'sections.settings',
    entries: [
      {
        kind: 'group',
        id: 'settings',
        labelKey: 'items.settings',
        icon: 'settings',
        links: [
          link('connections', '/settings/connections'),
          link('categories', '/settings/categories'),
          link('rules', '/settings/rules'),
          link('alerts', '/settings/alerts'),
          link('preferences', '/settings/preferences'),
          link('ai', '/settings/ai'),
          link('backup', '/settings/backup'),
        ],
      },
      link('notifications', '/notifications', 'bell'),
    ],
  },
]

function translateLink(spec: LinkSpec, t: TFunction<'menu'>): NavLinkEntry {
  return { kind: 'link', id: spec.id, to: spec.to, label: t(spec.labelKey), icon: spec.icon }
}

/** Builds the translated menu sections from the static table. */
export function buildMenuItems(t: TFunction<'menu'>): NavSection[] {
  return MENU.map((section) => ({
    id: section.id,
    title: t(section.titleKey),
    entries: section.entries.map((entry): NavEntry =>
      entry.kind === 'group'
        ? {
            kind: 'group',
            id: entry.id,
            label: t(entry.labelKey),
            icon: entry.icon,
            links: entry.links.map((child) => translateLink(child, t)),
          }
        : translateLink(entry, t),
    ),
  }))
}

/** Translated menu sections; re-translated when the language changes. */
export function useMenuItems(): NavSection[] {
  const { t } = useTranslation('menu')
  return useMemo(() => buildMenuItems(t), [t])
}
